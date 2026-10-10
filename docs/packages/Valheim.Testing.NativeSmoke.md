# Valheim.Testing.NativeSmoke

[Current preview API reference](https://tvongaza.github.io/ValheimTesting/). This command-line tool's supported entry points are the commands documented below, rather than a public .NET library surface.

*Assistant-written (Claude).*

`valheim-test`, a .NET tool from [ValheimTesting](https://github.com/tvongaza/ValheimTesting): one-command disposable checks that a mod loads in the real game, an A/B run that isolates a load interaction between two mods, an editable consumer project for your own assertions, and the commands that look after the machines a native test runs on. Every run copies the game into a disposable install, pins what it stages, keeps private evidence, and removes what it made. A pass means the selected plugins loaded and a client entered the world; it says nothing about a mod's gameplay. For a step-by-step single-mod and mod-conflict investigation, including how to read failed setup versus failed gameplay, see [Debug a mod load or mod conflict](https://github.com/tvongaza/ValheimTesting/blob/main/docs/debugging-mods.md).

| Command | What it does |
|---|---|
| `start [--mod DLL] [--output NEW_DIR]` | A client-side mod (or a selected pair) loads in an owned client hosting a disposable world; `--compare-mod` runs a second build of it against the same world |
| `server-load --mod DLL` | A server-side mod loads on an owned dedicated server, and one clean client (the mod absent) joins it |
| `build --project MOD.csproj` | Publicize the installed game's required compile references privately and build a mod without copying its DLL into the live game |
| `server-load-ab ... --remove-mod DLL` | The same load with and without one mod, to isolate a load interaction |
| `finish --run ID` | Ask the live owner of a foreground `--hold` run to finish and clean up |
| `init [server] --output NEW_DIR` | An editable project that reruns a `start` or `server-load` run with your own assertions |
| `env list`, `env preflight` | What the environment inventory holds (this machine with no file), and whether a one-off can run on it |
| `env status`, `env recover`, `env teardown` | What earlier runs left on each host, from their run journals, and clearing it |
| `session check SESSION [--hosts]` | A session file (several actors on several machines) checked before anything is copied |

`valheim-test help` lists the commands; a command given wrong options prints its whole usage. If it does not list a command named here, the installed tool predates it: `dotnet tool update --global Valheim.Testing.NativeSmoke --prerelease`. Exit codes: 0 passed (for `env` and `session check`: nothing to refuse), 1 a run failed, 2 a usage error, 3 refused before the run, or a run whose outcome is unknown (read its evidence; `env status` shows what it left), and for `env` and `session check` something refused or left to recover.

## Install

```sh
dotnet tool install --global Valheim.Testing.NativeSmoke --prerelease
```

The built-in `start` and `server-load` scenarios run from the tool's assemblies; they do not restore an editable consumer. `start --mod DLL` needs no NuGet.org access, while `server-load` may restore packages to build its adapter unless you supply `--adapter DLL`. The new `--project`, `--scenario-project` and `--adapter-project` forms build local projects and may need their declared NuGet dependencies. On Homebrew macOS installs, a global .NET tool may need `DOTNET_ROOT` set to the directory reported by `dotnet --info` before its apphost launches.

## One machine: build and run

### Build a mod against the installed game

For a mod that needs publicized game assemblies, run `build` from the checkout before the native test. It detects the local Steam Valheim install unless you give `--game`. It reads the project's publicized reference names, writes those assemblies under a new private output folder, and passes their paths to MSBuild. `BepInEx.dll`, Harmony and declared third-party references such as Jotunn are copied to that private folder. The original Valheim install, including a signed macOS app bundle, is only read. No game assemblies or third-party DLLs belong in Git.

```sh
valheim-test build --role server --project /path/to/MyServerMod/MyServerMod.csproj --output /private/runs/mod-build
# Omit --role server for a client mod. --game DIR selects a particular installed client or server.
# If more than one different Jotunn.dll is installed, select the intended one:
valheim-test build --project /path/to/MyMod/MyMod.csproj --dependency Jotunn=/path/to/Jotunn.dll
```

The command prints the built mod and `build-inputs.json`. Pass both to a native run using `--mod` and `--build-inputs`: the run refuses a changed mod, publicized reference, original game assembly, loader core or third-party compile reference before launch. Build a server mod against the dedicated server selected for its run; the client and dedicated-server assemblies can differ even at the same displayed game version. Each build makes fresh private references, so an older game's publicized copies cannot be silently reused. `build-inputs.json` records the full game-code SHA256, each reference hash and the publicizer version. It and the build log contain local paths and stay in private evidence. A missing third-party dependency is named and must be installed or selected explicitly; the tool does not download mod DLLs. The native run uses each selected compile dependency as an exact DLL choice, even when a search directory contains a different build. Give `--search-root DIR` for dependencies kept elsewhere, or `--search-root FILE.dll` to choose one exact build from an ambiguous directory.

The mod's project must accept command-line MSBuild properties for its game path, managed path, BepInEx core, publicized path and deployment destination. `build` sets `ValheimPath`, `ValheimManaged`, `BepInExCore`, `PUBLICIZED_PATH`, `BEPINEX_CORE` and `CopyOutputDLLPath` for existing project conventions; the deployment destination is a private folder. For projects importing the [game-reference source files](../../tools/game-references/README.md), a `ValheimReference` may point at a publicized file under the supplied `PUBLICIZED_PATH`. A project that hardcodes a live plugin copy outside these properties must remove or guard that copy before using this command.

### Run a mod-owned scenario with the same preparation

Put one public `IOneShotServerScenario` implementation in a .NET 10 test project. Its `RunAsync(GameSession, OneShotServerContext)` method receives the already prepared server and optional clean client, the exact fixture UID and the run report. For a server-only assertion, return `false` from `RequiresClient` and pass `--server-only`; for a client scenario, return `true`, omit `--server-only`, and open the prepared `context.ClientPlan` through `session.OpenClient`. The toolkit remains responsible for the actor processes, pins, private evidence and cleanup, including when the assertion throws. The [compiling example](../../examples/OneShotServerScenario/MetadataScenario.cs) shows the small server-only shape.

After cloning a mod and its test project, one command can build the mod against the selected dedicated server, build the scenario project and run them together:

```sh
valheim-test server-load --server-only \
  --project /path/to/MyMod/MyMod.csproj \
  --scenario-project /path/to/MyMod.SystemTests/MyMod.SystemTests.csproj \
  --adapter-project /path/to/MyMod.TestAdapter/MyMod.TestAdapter.csproj \
  --adapter-property 'MyModDll={mod}' \
  --session-capability mymod.testing/session \
  --session-token-variable MYMOD_TEST_SESSION_TOKEN \
  --dependency Jotunn=/path/to/Jotunn.dll \
  --server-startup-seconds 900 \
  --output /private/runs/my-mod-scenario
```

`--adapter-project` is optional for a scenario that needs no mod-specific game command. It builds the adapter privately against the pinned ValheimCLI core (`CliDll`) and mod (`ModDll`). If the adapter's project uses its own property for the mod reference, map it once with `--adapter-property 'MyModDll={mod}'`; `{cli}` also selects the pinned CLI file. Only those two pinned placeholders are accepted. Name the custom adapter's registered session capability and token variable; the one-shot preflight refuses to start a game without them, because the built-in adapter has a different identity. `--dependency` is needed only for a compile dependency the server installation does not already contain. No arbitrary mod DLL is downloaded. Use `--preflight-only` to resolve the same pins and validate the scenario without starting Valheim. If the mod and scenario DLLs are already built, use `--mod DLL --build-inputs FILE --scenario DLL` instead. The prepared `campaign.json`, `plan.json`, dependency locks, `build-inputs.json`, adapter inputs, scenario source hashes and native result remain under the private output. On another host, run the same one-shot command from the checkout with a new output folder; its own inventory and game build are selected and pinned there. Compare the two hosts' recorded plans and hashes before treating their results as the same test input. For an exact pinned campaign replay, copy its private inputs to the other host, verify that host's game and mod hashes, and use `valheim-test init server --output NEW_DIR` to create an editable consumer for `campaign.json`; adapt its scenario and environment binding there. A different game build requires a new local mod build; changing a hash by hand is not a valid rerun.

For a mod tested in a world hosted by the client, implement `IOneShotHostedScenario` instead. Its `Run(ClientRound)` receives the joined hosting client, report, output and fixture UID; add assertions with `round.Step`. The [hosted example](../../examples/OneShotServerScenario/HostedWorldScenario.cs) compiles in the same sample project. Run it on a Windows or macOS client install with the desktop and Steam available:

```sh
valheim-test start \
  --project /path/to/MyMod/MyMod.csproj \
  --scenario-project /path/to/MyMod.SystemTests/MyMod.SystemTests.csproj \
  --dependency Jotunn=/path/to/Jotunn.dll
```

`start` selects a client install and builds against that client's game assemblies. `server-load` selects a dedicated-server install and builds against its assemblies. Both commands use the same private build and scenario loader, and both pin the resulting DLL for that run. For the edit–test loop, keep the command and edit the mod or assertion project between invocations: each invocation compiles the current source, creates a fresh output folder by default, and writes a new hash. No standing mod pin has to be updated. An old result remains tied to its earlier build; an already built `--mod DLL --build-inputs FILE` run still refuses changed bytes until it is rebuilt.

For a mod that generates a network on first world load, `--server-startup-seconds 900` gives the dedicated server a bounded 15 minutes to become ready (default 300 seconds; maximum 1800). Use a small mod for routine edit-loop checks, and reserve the longer budget for that mod's actual generation scenario.

The usual setup is one Windows PC with Valheim and the free Valheim Dedicated Server installed through Steam, each with BepInExPack_Valheim, and the Steam client running and signed in. Nothing else is written by hand: the machine's installs, ports and folders are detected, and `valheim-test env list` prints what it found, what it assumed and every path it tried for anything missing ([this machine, with no file](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.Game.md#this-machine-with-no-file)). `valheim-test env preflight` checks the local actors before any copy: the run journal and host lock, conflicting game processes, ValheimCLI ports, the client desktop, and whether Steam is running for a client. `start` and `server-load` use the same local checks. A running Steam process or a remembered login does not prove an active sign-in, so preflight does not claim to verify one. After launch, a fresh `SteamAPI_Init() failed` line in the client's BepInEx log ends startup promptly and is kept with the run's evidence. Dedicated-server-only runs do not need Steam.

For a server-only mod, `server-load` loads it on an owned dedicated server and, by default, joins one owned clean client (ValheimCLI only, the mod pinned absent). On one Windows PC with Valheim and the free Valheim Dedicated Server installed through Steam, each with BepInExPack_Valheim installed (or the loader as `--loader-package`), nothing else is needed; valheim-test brings its pinned ValheimCLI when its build carries the bundle (otherwise give `VALHEIMCLI_BUNDLE`):

```sh
valheim-test server-load --mod /path/to/ServerMod.dll
```

You can invoke `valheim-test start` through SSH on a Windows PC. Before it copies a fixture or game, it checks that the SSH user has exactly one desktop session and Steam is running there. From SSH's session 0 it starts the owned client in that desktop through a temporary scheduled task. From an ordinary terminal already in a desktop session, it uses the direct owned-client launch instead. If Steam is absent or the session changes between the check and launch, the run refuses. Leave the desktop unlocked for a native run; a locked desktop can leave the game stalled at its first scene, which the startup deadline and kept logs will report. On shutdown, a session-0 run asks the game to close through a second short-lived desktop task. The run records whether the window close was actually requested and whether the client quit cleanly or needed a kill; process identity is checked again before either action. Its logs are kept with the result.

## A client-side mod: start

```sh
dotnet tool install --global Valheim.Testing.NativeSmoke --prerelease
valheim-test start \
  --game /path/to/prepared/Valheim \
  --mod /path/to/MyMod.dll \
  --output /path/to/a/new/private-run
```

On macOS, `start` requires the unlocked console user's Aqua session before it records the run or copies the game. Launch it from that desktop; an SSH or locked session is refused immediately. A missing fresh BepInEx startup log remains a separate failure after launch.

When `start`, `server-load`, or `server-load-ab` refuses after creating its safe output directory, it writes `REFUSED.txt` with the reason. Treat that folder as failed evidence, and check `valheim-test env status` before another run; a refusal marker does not replace the run journal's cleanup check. A path rejected as inside a game install or account directory is never written to.

On a local Apple Silicon Mac, `start`, `server-load`, and `server-load-ab` default to native arm64; Intel Macs default to x64. An environment inventory can choose either slice on its client recipe, and `--client-architecture x64|arm64` overrides it for one run. Select x64 explicitly when a mod or loader requires Rosetta. `env list` prints the selected slice and the slices the local game and selected loader support. `env preflight` and the one-shot commands refuse a missing arm64 game, Doorstop, or native BepInEx core before copying the game; they never silently fall back to Rosetta. The derived `regression.json` or `campaign.json` and `client-plan.json`, `client-process.json`, and the result provenance record the chosen slice. See [native Apple Silicon setup](../platforms.md#native-apple-silicon-client).

`start` allows 180 seconds for the hosted world to open by default. A mod that generates a world on first load may need a longer bounded deadline, for example `--join-seconds 600` (allowed range: 10–900). The run records the chosen value in `regression.json` and still fails if the game never enters the world.

While a Windows `start` run is still using its owned desktop client, a second terminal can inspect it with `valheim-test cli --evidence RUN/evidence/smoke --phase menu --command "cli_manifest"`. Use `--phase world` once its hosted world is entered, or `--expect-strict FILE` for other independently pinned world states. The passthrough checks the exact process start identity and the selected strict pins, issues only one command and never retries it; `--timeout-seconds 1..15` bounds a command. Commands can change the game. Each invocation creates a private `owned-cli-command-*.jsonl` record beside that run's evidence **before** sending its strict pin check or command, and prints its path; review the record before sharing evidence. It refuses after the owned process stops. A failed startup or world entry also keeps `client-failure-diagnostic.json`, log tails and, only for a still strictly pinned client that can service the command and permits cheats, a `cli_screenshot` image before teardown. See [the agent diagnostic recipe](../agent-guide.md#diagnose-an-owned-client-that-does-not-reach-its-menu-or-world).

### Keep a passing run open for inspection

Add `--hold` to `start` or `server-load` (including `server-load --server-only`) to keep its owned game running **in the foreground** after the normal load or join checks pass. The command prints a run ID. From another shell on the same runner machine, signal the original runner to close the game and perform its normal cleanup:

```sh
valheim-test start --mod /path/to/MyMod.dll --output /path/to/new/private-run --hold
# In another shell, using the run ID printed above:
valheim-test finish --run run-YYYYMMDDTHHMMSSZ-1234abcd
# Wait for the original foreground command to exit, then check:
valheim-test env status
```

On Windows, inspect the held client through `valheim-test cli --evidence /path/to/new/private-run/evidence/smoke --phase world --command "cli_screenshot inspection"`; that command still checks strict pins and records its use in private evidence. `server-load --hold` keeps both the dedicated server and its clean joined client running; `--server-only --hold` keeps only the server. If any owned game exits unexpectedly during the hold, the run records a failed step and tears down instead of waiting for a finish request. Ctrl+C on the foreground runner also initiates its teardown. A killed terminal is not a successful finish: `env status` shows its journalled copies and exact process, and `env recover --run ID` is the recovery path once the owner is proven gone. Successful recovery also removes that dead run's hold marker. This is an interactive inspection mode, not a detached session or a replacement for a mod's assertions.

The experimental Windows `detach server-load` starts a **server-only** held run under Task Scheduler, so the invoking terminal or SSH session can exit. It requires one signed-in desktop session for the runner's task, but the Steam client is not required for a dedicated server. Supply absolute server, mod and output paths; the output must be new. It returns only after the exact pinned server has reached the held state:

```powershell
valheim-test detach server-load --server 'C:\Games\Valheim dedicated server' --mod 'C:\Mods\MyMod.dll' --server-only --output 'C:\ValheimTesting\runs\inspection-1'
valheim-test detach status
valheim-test detach finish --run RUN_ID
# If setup failed before a run ID appeared, inspect env status, then:
valheim-test detach recover --token TOKEN
```

The command snapshots the runner and selected mod directory (including adjacent managed dependencies) into ValheimTesting's private data folder before launch. It refuses a mod directory over 512 MiB or 10,000 files. Additional explicit input paths, such as a custom world or loader package, are not staged in this first mode; use foreground `--hold` for them. `detach finish` asks the exact live owner to stop, waits for its exit, runs the usual journal recovery check, removes the OS job and keeps its logs in the private output. If the owner died after recording the run ID, finish uses exact journal recovery to stop its surviving owned game and exits with failure; it never reports that interrupted run as passed. `detach recover --token` covers a failure before the run ID was recorded and refuses while its OS task may still be running. `env status` remains the authoritative view of owned state. This first detached mode does not restart a session, swap a build, attach a client or send ad-hoc commands; use a scripted scenario for those checks.

The selected game must already have a coherent BepInEx/Doorstop loader, or use `--client-loader-package` with a [reviewed extracted loader set](https://github.com/tvongaza/ValheimTesting/blob/main/docs/bepinex-loader-package.md). Two loader faults are handled for you. Sometimes an install on this machine has a Doorstop proxy and configuration from different Doorstop versions (a mod manager such as Gale or r2modman swaps the proxy and leaves BepInExPack's file), or a proxy no check recognises. Or its BepInEx is older than 5.4.23.5 (a stock BepInExPack_Valheim 5.4.2202 server install, for example), which on Valheim's Unity 6 cannot reach Unity's log writer, so its plugins' lines never reach Unity's log and it logs `Unable to start Unity log writer` at startup ([BepInEx#755](https://github.com/BepInEx/BepInEx/issues/755), fixed by [#1264](https://github.com/BepInEx/BepInEx/pull/1264) in 5.4.23.5). Then its disposable copy takes the BepInExPack_Valheim this tool ships, which carries BepInEx 5.4.23.5 ([loader-dependency.json](https://github.com/tvongaza/ValheimTesting/blob/main/loader-dependency.json), unmodified, notices in THIRD-PARTY-NOTICES.md). The run prints one line naming why and the package, and records it in the run's provenance; the install itself is never changed. Every other loader problem still refuses. It also needs one ValheimCLI core-and-pack set: `--cli-manifest` with `--cli-files` from one build, or `VALHEIMCLI_BUNDLE` naming a folder with its manifest and DLLs, or else the bundle pinned in this repository's `cli-dependency.json` (`bundle`: the asset's URL and SHA-256), which a build embeds and a run extracts once under ValheimTesting's own folder (`cli/<commit>`) and checks file by file. The run prints which one it used. A ValheimCLI already installed in the game is never picked up on its own; name it with `--cli-files` to use it. It never silently downloads or mixes plugin builds. Every one-shot path (`start`, `server-load`, `server-load-ab` and fixture bake) stages the pinned bundle's core **and every pack**, even if the particular command does not use a pack. The dependency lock records every staged pack's SHA-256; a missing or changed pack is refused before the game is copied. This keeps the staged ValheimCLI set identical across commands and avoids command-name guesses for scripted or mod-owned commands. `start` runs on the inventory's client environment: this machine's Valheim with no `--inventory`, `--game` as a local install override, or a file's (`--client-env NAME` picks one); `--client-loader-package` overrides that selected client's loader; it must be on this machine. It prints what it detected. Its ValheimCLI port is the environment's, as printed, and the character is checked against this machine's detected Steam userdata. The chosen environment is written beside the run's `regression.json` as `environments.json`, so its consumer and bundle use the same machine. Each run has its own journalled disposable install, `<runtime>/vt-prep-regression-native-smoke-<time>-<run-id>/runtime`. On Homebrew macOS installs, a global .NET tool may need `DOTNET_ROOT` set to the directory reported by `dotnet --info` before its apphost launches. `init` needs the .NET 10 SDK and NuGet.org to build its generated project, as does `server-load`'s adapter build without `--adapter`.
Repeat `--mod DLL` for a selected client-side pair. Repeat `--search-root DIR` for explicit local dependency trees, or name one exact DLL with `--search-root FILE.dll`. Byte-identical copies of a dependency with the same assembly and plugin identity count as one choice; the lock records every discovered source path and stages just one. Different builds still require an explicit choice. If an assembly reference is used only behind a disabled soft integration, repeat `--optional-reference ASSEMBLY` to confirm that deliberate omission; the dependency lock records each decision. Use `--client-loader-package FILE` for a captured BepInEx/Doorstop package. The ValheimCLI port is the client environment's, which the inventory chooses and the run prints. `--expected-log-error` applies to the mod-bearing actor (client for `start`, server for `server-load`); it accepts a known benign BepInEx error's **whole, exact header line** only when paired with `--expected-log-reason`. Every other error still fails. Every input is read, never edited. The output directory must be new. Its `dependencies.lock.json` records unresolved choices even if setup stops; a ready run also keeps its manifest, source world and character, and `evidence/`. The owned game copy is removed at the end; an incomplete unmarked copy is left for inspection instead of being deleted blindly.

To compare two builds of the first selected mod, add `--compare-mod /path/to/NewBuild.dll --compare-source NEW_COMMIT`. The command runs `before` and `after` against the same world and companion DLLs, retaining a separate result for each. It refuses a changed companion, dependency, optional-reference decision or ValheimCLI build before launch. Both builds must declare the same plugin identities; this is a build comparison, not an add/remove-mod comparison. A failed first arm stops the comparison and keeps its evidence.

From a directory containing exactly one mod project, `start` or `server-load` can omit `--mod`: the tool inspects the newest built output and selects its single `[BepInPlugin]` DLL. It refuses several projects, missing or ambiguous plugin outputs, and source newer than the DLL; build first or give an explicit `--mod DLL`. The selection reason is printed and recorded in the run's provenance. `server-load-ab` still needs explicit mods, because its two arms must name the same fixed inputs. Without `--output`, either one-shot command creates a new timestamped directory under this machine's ValheimTesting data root (`valheim-test-runs/`); the command prints the full path. An explicit `--output` inside the mod project is refused so generated adapter sources cannot become part of the next mod build.

## A server-side mod: server-load

The actors come from the [environment inventory](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.Game.md#this-machine-with-no-file). With no `--inventory`, that is this machine: its Steam installs, the ValheimCLI ports 5688/5689 and the game port 2486, the packaged smoke world and the packaged disposable character. The command prints what it detected and chose (`detected:`, `server:`, `client: local-client (default; --server-only to skip)`). It then writes the campaign it derived to `campaign.json` in the output (with `environments.json` when `--server`/`--client` override the detected installs) and runs the read-only host preflight, all before anything is copied. A client that cannot run (no Valheim install, Steam not running or signed in) is refused with the reason; the client is never dropped silently. `--server-only` skips the client's join. `--preflight-only` stops after the preflight. `--output` defaults to a new `valheim-test-runs/<UTC time>` directory under this machine's ValheimTesting data root, outside the mod project. `--inventory FILE` uses a file's environments instead (`--server-env`/`--client-env NAME` pick ones other than the first). The server must be on this machine, because the mod is resolved and its adapter built against the server's own install; a server elsewhere runs as a campaign or with `PinnedServerRun --inventory`. A client on another machine needs `--join HOST:PORT`, this machine's address as that host reaches it. A local macOS dedicated server uses the same campaign path; its fixture is placed in the signed-in user's default `worlds_local` only after collision checks and is moved into the run's evidence after the server stops. Remote macOS server hosts are not supported yet.

`--server DIR` and `--client DIR` point at other installs on this machine:

```sh
valheim-test server-load \
  --server /path/to/prepared/dedicated-server \
  --mod /path/to/ServerMod.dll \
  --client /path/to/prepared/client \
  --output /path/to/a/new/private-server-run
```

The source server and client may instead be unmodded installs. Supply reviewed, extracted loader manifests for each platform and one coherent ValheimCLI bundle explicitly:

```sh
valheim-test server-load \
  --server /path/to/unmodded/dedicated-server \
  --client /path/to/unmodded/client \
  --loader-package /private/server-loader.json \
  --client-loader-package /private/client-loader.json \
  --cli-manifest /private/ValheimCLI/cli-capabilities.json \
  --cli-files /private/ValheimCLI \
  --mod /path/to/ServerMod.dll \
  --output /private/runs/server-load-1
```

`--loader-package` selects the server's BepInEx core for dependency resolution and adapter compilation. Without it, the chosen server environment's loader package is used; otherwise the tool uses its reviewed shipped BepInExPack when the source install needs a coherent loader. `server-load-ab` makes this same server choice once, uses that core for both dependency locks and adapter compilation, and passes the selected server package to both arms. Its results record why a shipped package replaced the source loader. The client package is separate because a dedicated server's loader files are not assumed to work in the client. Each arm independently selects and records its client package; it is pinned within that arm, but is not frozen across arms. Packages are applied only to disposable copies. The command still needs a compatible game build and local, reviewed loader and ValheimCLI files. It does not download a dependency. `server-load-ab` accepts the same setup options except `--hold` and `--preflight-only`.

The command builds its [test-only adapter](https://github.com/tvongaza/ValheimTesting/blob/main/src/Valheim.Testing.NativeSmoke/SessionAdapter/README.md) from source embedded in the tool against the exact game and ValheimCLI core it selected. Building the adapter restores .NET Framework reference assemblies from NuGet.org; to run offline, build it once and pass it with `--adapter DLL`, which also covers an independently built adapter. The command copies the dedicated runtime, clears plugins, scripts, configs and patchers **in the copy**, then stages the selected mods, their resolved dependencies, ValheimCLI and the adapter. Unless `--server-only`, it copies a separate client install containing ValheimCLI alone, stages the clean disposable character for that run, and requires the client to join with every selected server plugin pinned absent. Both owned processes, the copies and the staged character are cleaned up on success or failure. With `--server-only`, it only checks server load and socket readiness, printing `SERVER_LOAD_PASS` rather than a client-join pass. Like every owned server, its server gets test access on each boot (devcommands, then `confirmcheats`, each verified); the load smoke itself issues no other test command and does not request admin-only player protection. Its disposable server plan waits at most 20 seconds to quit before a recorded kill; it makes no save-on-quit or crossplay-retirement claim. Use the full server runner and its shutdown assertions for those claims.

Repeat `--mod`, `--search-root` and `--optional-reference` as above. Use `--config FILE` for chosen server settings, such as disabling expensive world generation when it is irrelevant to the setup check. Use `--plugin-file FILE` and `--plugin-dir DIR` for a mod's external assets beside its DLL in `BepInEx/plugins`. The staged configs and asset bytes enter the strict runtime manifest; DLLs inside asset directories are refused so they cannot bypass dependency resolution. The output contains private plans and evidence, including a password in `plan.json`; do not publish it.

For a server-only run, `--world-fixture DIR` loads a pinned copy of a named world instead of the packaged smoke world; if that world has a bake manifest, the runner verifies its UID and file hashes before use. To save generated state as a new fixture, add `--bake-fixture NEW_DIR` and a strict `--assert-command TEXT --assert-line PREFIX` that proves the mod is ready. An optional `--before-save-command TEXT --before-save-line PREFIX` makes one deliberate change first. The built-in `cli_zdos_at`, `cli_containers_at`, `cli_prefabs_at`, and `cli_piece_support` observations require the pinned ValheimCLI World Tools and Observe packs; a mod's own command does not. The runner confirms an advancing save, gives the server up to five minutes to save again at clean quit, reserves ten minutes for cleanup including world fetch and runtime retirement, checks the fetched world's UID and hashes, then writes the new fixture with both the confirmed and exported save numbers. It refuses an existing destination or an unconfirmed save. See the [fixture-bake recipe](../fixture-authoring.md#bake-a-saved-fixture-with-an-owned-server) for commands and the persistence check.

Disk use: a `server-load` holds one staged server copy, which the run runs from, and, with `--client`, the staged clean client, about 2 GB and 4 GB on Windows. Its preflight refuses before the first copy when the drive of each environment's `runtime` folder, on its host, lacks that room plus headroom for every copy going there (on a Mac, the drive of the environment's `runtime` folder). When it ends, the run removes the server copy after keeping what the run wrote in `evidence/runtime-changes/`, and the staged client is removed ([what the output keeps](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.GameSessions.md#pinned-server-runner)). So a finished run keeps megabytes, not gigabytes, and the final line prints the evidence's size. Set `VALHEIM_TESTING_KEEP_RUNTIME=1` to keep the run's whole runtime copy for debugging.

## Isolate a load interaction: server-load-ab

To isolate a load interaction, change `server-load` to `server-load-ab` and add `--remove-mod /path/to/SecondMod.dll`. The command resolves both sets before launch, runs the full set in `before/`, then runs the same fixture without that one mod in `after/`. It keeps the surviving mod files, ValheimCLI, server/client installs, configs and selected assets pinned between arms; dependencies used only by the removed mod may disappear. A native failure in the full-set arm still runs the removal arm and keeps both results; an input/setup refusal stops before the second arm. A difference narrows a load interaction but does not assign fault to a mod. Both arms are load/join checks, so use small fixture plugins for routine runner validation and reserve expensive real-mod generation for a specific regression.

## Your own assertions: init

`valheim-test init --output NEW_DIR` creates the hosted consumer, for the `regression.json` a `start` run writes (with the `environments.json` beside it); `valheim-test init server --output NEW_DIR` creates the server consumer, for a `server-load` run's `campaign.json` (with the unbound `plan.json` and `client-plan.json` beside it). `init` builds a test consumer; `build` builds the mod itself using private references. `init` pins the `Valheim.Testing.Game` this tool runs, so the consumer reads the tool's files, and restores and builds it from NuGet.org alone. A tool built from a source checkout pins its unreleased Game version, which NuGet.org does not serve, so `init` refuses there; use a released tool.

## What runs left: env status, recover and teardown

`server-load`, `server-load-ab` and every session journal their copies, characters, processes, Steam leases and host locks before making them. Every `start` run does the same for its disposable game copy, character and exact client process. Normal cleanup retires each entry. After an interruption, `valheim-test env status [--inventory FILE] [--json]` lists what remains without changing it ([what runs left](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.GameSessions.md#pinned-server-runner)); `valheim-test env recover --run ID` removes only what it can prove belongs to the run.

A character intent without a completed stage is not proof that a same-named save belongs to the run. For `start`, recovery checks the registered source's SHA-256 before deleting an incomplete copy; a missing file is cleared, while a differing file or extra backup is left for inspection. A hosted character whose stage never confirmed completion is never deleted by name, even if its bytes match the registered source: it could have been a personal save already present when staging began. Move it out of `characters_local` for safekeeping after inspection, then retry recovery. A plain `start` killed midway through copying the game leaves an incomplete install without its final marker. A separate claim written before copying lets recovery remove only that run's partial copy. Without a matching claim, recovery leaves the directory untouched. A copy from before runs journalled them can be inspected and removed with `env teardown --copy PATH` ([copies left behind](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.GameSessions.md#pinned-server-runner)).

Recovery checks a process's ID, start time and command line before stopping it. `env teardown --run ID` also removes what a run kept on purpose. A second Ctrl+C during cleanup exits with code 3, leaving a recoverable journal entry ([cancellation](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.GameSessions.md#pinned-server-runner)). `session check --hosts` checks every host the session needs; `env preflight` reads this machine's journal and any local hosts in its inventory. Both refuse a running or unrecovered run and name the run to recover.

## Several machines: sessions

A server and clients on several machines run as a session: an inventory file (the machines, how to reach them, and the installs on each: [several machines](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.GameSessions.md#several-machines)) and a session file, `session.json`, that names the actors (a dedicated server and named clients, or a client hosting the world and its peers), each one's dependency lock and disposable character, and the world fixture ([a campaign: remote clients and Steam identities](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.GameSessions.md#a-campaign-remote-clients-and-steam-identities)). `valheim-test session check SESSION [--json]` reports every independent problem with the inputs and the actor assignment without contacting a host; `--hosts` adds the read-only host checks: the selected installs and loaders, ValheimCLI ports, free space for the copies, a game already running where a client needs the desktop session, the run journals, and each client host's signed-in Steam account. Mutable state is checked again under the host locks and Steam leases before launch.

The scenario itself is the mod's: it runs from the mod's own test project on the toolkit's `GameSession` (copy the [FullLifecycle example](https://github.com/tvongaza/ValheimTesting/blob/main/examples/FullLifecycle/README.md)'s `GameSessionFixture`, an xUnit adapter for one session per test class, and its `ExampleSession`, which finds `session.json` beside the tests and skips the native test without it). The [native acceptance suite](https://github.com/tvongaza/ValheimTesting/blob/main/tests/Valheim.Testing.NativeAcceptance/README.md#prepare-the-campaign) has a server-plus-two-clients test: setup, exercise, assertions and teardown, with explicit waits and a rejoin.

- **Shared machines.** A host runs one owned run at a time (its lock); every actor on a host gets its own ValheimCLI and game port, its own copy and its own disposable character. A client needs the desktop session of a signed-in Steam account, so one desktop session runs one client; two clients need two accounts, read from each host and refused when shared ([Steam account leases](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.GameSessions.md#steam-account-leases)). The toolkit never signs in to Steam: the right account signed in on each client host stays a person's step that `session check --hosts` reports.
- **Hosted and read-only clients.** A client can host the world instead of a dedicated server, its peers joining it ([a hosting client in a session](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.GameSessions.md#a-hosting-client-in-a-session)); an attached client (one its operator started) is driven but never stopped, its install neither read nor changed, and it keeps devcommands only ([actors, fixtures and reports](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.Game.md#actors-fixtures-and-reports)).
- **macOS.** A Mac runs one client actor. A local Mac dedicated server uses the same hosted campaign path as Linux and Windows; the native check passed with Steam running and signed in on that Mac. The server depot lacks a local `steamclient.dylib`. A disposable server copy with SteamCMD's local library dependency set, including `crashhandler.dylib` and Breakpad, loaded its local `steamclient.dylib` and opened the Steam server. Steam was still open, so Steam-free operation needs a separate native run and the toolkit does not yet stage that set automatically. Remote Mac servers and Mac crossplay are not yet supported.

## What a result says

Every run writes `result.json` and `junit.xml` into its evidence: each step in its phase and the four states (preflight passed, runtime ready, scenario passed, cleanup verified), so a failed setup is never read as a failed mod ([phases and the four reported states](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.GameSessions.md#pinned-server-runner)). Its `Toolkit` names what ran: each toolkit package and the ValheimCLI transport with version, SHA-256 and whether it is `released` (built by the release workflow), a `candidate` or `unreleased` (a source build), and its `Plugins` the plugin pins each actor's game confirmed. Keep the evidence private: plans, manifests and game logs may hold local paths, passwords or account identifiers.

## Evidence on record

The packaged world and character were made by Valheim 1.0.16 and checked in a native client. Recheck them with a new game version. Each copy of the character has the same player ID, so run only one client with it at a time.

On a Windows 1.0.16 client, the command passed 12/12 steps with one selected example mod and 12/12 with that mod plus its test adapter. Both runs used strict pins, entered the packaged hosted world, and left no character, world copy or owned install behind. The station's pre-existing saves and launcher matched their before-run hashes. The first arm failed solely on a known BepInEx Unity-log-writer line, so the passing arms named that exact line and a reason; no general error suppression was added.

The dedicated-server path also passed a strict 17-step Windows 1.0.16 run with two selected server mods and a clean joined client containing neither mod. That is setup and interoperability evidence, not a gameplay result; use fast fixture mods for routine runner checks rather than repeating a long catalogue or world generation.

The tool carries the `Valheim.Testing.Game` it was built with; its generated consumer references that same version as a package rather than a project. Native setup/load results do not establish the mod's gameplay behavior. Keep the generated manifest and game logs private; they may contain local paths or account identifiers.
