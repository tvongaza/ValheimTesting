# Valheim.Testing.NativeSmoke

[API reference](https://tvongaza.github.io/ValheimTesting/reference/). This package is the `valheim-test` .NET tool; its supported surface is the commands below, not a .NET library.

`valheim-test` checks that a mod loads in the real game. Each run stages a disposable loader profile of a Steam install, keeps private evidence and removes what it made; the install is only read. New to the tool? Start with [Your first server load check](../tutorials/first-server-load-check.md).

## Install

```sh
dotnet tool install Valheim.Testing.NativeSmoke --version 0.3.0 --global
```

The tool needs the .NET 10 SDK. If a Homebrew .NET on macOS cannot start the tool, set `DOTNET_ROOT` to the directory `dotnet --info` reports.

## Commands

| Command | Does |
|---|---|
| [`start`](#start) | Loads a client-side mod or an explicit CLI-only control in an owned client that hosts a disposable world |
| [`server-load`](#server-load) | Loads a server-side mod on an owned dedicated server; one clean client joins |
| [`server-load-ab`](#server-load-ab) | Runs `server-load` with and without one mod, to isolate a load interaction |
| [`server-load-phases`](#server-load-phases) | Runs ordered server-only fixture bakes |
| [`build`](#build) | Builds a mod against the selected install's publicized assemblies, privately |
| [`init`](#init) | Creates an editable test project that reruns a `start` or `server-load` run |
| [`finish`](#hold-and-finish) | Asks a held run to finish and clean up |
| [`save-world`](#save-world) | Saves a held `start` run's world and copies it out, for a later `start --world-fixture` |
| [`cli`](#cli) | Sends one strictly pinned command to a running owned client or server |
| [`env`](#env) | Lists this machine's environments, checks it, and clears what runs left |
| [`session check`](#session-check) | Checks a session file and, with `--hosts`, its hosts |
| [`detach`](#detach) | Experimental: a held Windows dedicated server that outlives the terminal |
| [`steam-runtime capture`](#steam-runtime-capture) | Pins SteamCMD's macOS server libraries for a Mac server profile |

`valheim-test help` lists every command with its usage.

### Exit codes

| Code | `start`, `server-load`, `server-load-ab` | `env`, `session check` | `cli` |
|---|---|---|---|
| 0 | Passed | Nothing to refuse or recover | ValheimCLI accepted the command |
| 1 | The run failed | Not used | Not accepted, or the screenshot incomplete |
| 2 | Usage error (not `server-load-ab`) | Usage error | Usage error |
| 3 | Refused, or the outcome is unknown | Something refused or left to recover | Refused; no command sent |

`server-load-ab` reports a usage error, `--hold` and `--preflight-only` included, as `REFUSED:` without a usage line, and exits 3. `build`, `init` and `finish` exit 0 when done, 2 on a usage error and 3 when refused. `save-world` exits 0 when the world is saved and copied, 1 when the game was asked to save but no copy was made, 2 on a usage error and 3 when refused before anything was sent.

## Run options

`start`, `server-load`, `server-load-ab` and `build` share these options. A command refuses an option it does not take as `Unknown option`. "Repeat": give it more than once.

**Mod inputs**

| Option | Taken by | Does |
|---|---|---|
| `--mod DLL` | start, server-load, server-load-ab | A mod to load. Repeat for a set. Without a selection, `start` and `server-load` pick the current project's one built plugin; `start --no-mod` explicitly selects a control |
| `--no-mod` | start | A CLI-only control with no mod under test; no mod discovery or build |
| `--project MOD.csproj` | start, server-load, build | Builds the mod privately first; leave out `--mod` and `--build-inputs` |
| `--build-inputs FILE` | start, server-load | The `build-inputs.json` from `build`; the run refuses a changed mod or reference |
| `--dependency NAME=DLL` | start, server-load, build | A third-party compile reference for `--project`. Repeat |
| `--config FILE` | start, server-load, server-load-ab | A config file for the mod-bearing actor, copied into `BepInEx/config` by basename. Repeat; `start` takes `.cfg` files |
| `--env NAME=VALUE` | start, server-load, server-load-ab | A non-secret variable set explicitly on every owned game process. Repeat for distinct names |
| `--search-root DIR\|DLL` | all four | A folder or exact DLL to search for dependencies. Repeat |
| `--optional-reference ASSEMBLY` | start, server-load, server-load-ab | Confirms a reference that is deliberately absent. Repeat |
| `--scenario TEST.dll`, `--scenario-project TEST.csproj` | start, server-load | A mod-owned scenario; one or the other ([scenarios](#your-own-scenario)) |
| `--output NEW_DIR` | all four | A new folder outside the mod project. Default: a new folder under `valheim-test-runs/` in ValheimTesting's data folder |

The [dependency resolver](../reference/game-run-inputs.md#resolve-mod-dependencies) chooses files and writes `dependencies.lock.json`. It never downloads a mod.

**Explicit config and environment inputs**

`start --config` configures the hosting client. `server-load --config` configures the dedicated server; the joining client keeps its clean profile. `--env` reaches the hosting client for `start`, and both the server and any joining client for `server-load`. `--client-env` and `--server-env` select inventory recipes; they do not pass variables.

```sh
valheim-test start --mod /path/to/MyMod.dll \
  --config /path/to/example.mod.cfg --config /path/to/BepInEx.cfg \
  --env 'EXAMPLE_MODE=selected value=one' --env EXAMPLE_TRACE=1 --hold
```

Config destinations must be distinct without regard to case. Missing or linked files and ValheimCLI's runner-owned config are refused before host preflight or builds. An explicit `BepInEx.cfg` replaces the disposable loader's default. Source configs and the source install are never edited. Config bytes are retained once before launch; `start` comparison arms reuse those bytes, and staging checks each retained copy against its original SHA-256, including after asynchronous host checks. `server-load-ab` pins the originals and retained copies between arms.

Environment names use letters, digits and underscores, starting with a letter or underscore, and must be distinct without regard to case. Values split at the first `=` and preserve spaces, quotes, Unicode and shell punctuation literally. Empty values, NUL, line breaks, `{runtime}`, `{world}` and `{port}` are refused on every route. Loader-owned names, including the entire `DOORSTOP_` and `DYLD_` namespaces (case-insensitive), `LD_LIBRARY_PATH` and `LD_PRELOAD`, session GUIDs and built-in or selected adapter authentication variables are also refused. Quote values for your calling shell. Direct launches can inherit their caller's environment; desktop tasks and remote hosts have different inheritance, so use `--env` for reproducible selected inputs. The tool forwards only explicitly supplied variables through these flags.

These flags take **non-secret data**: the exact values, config source paths, retained config copies and SHA-256 hashes remain in private `run-inputs.json` and `inputs/configs/` after runtime cleanup. Each result links the record and its hash from provenance; a changed record is refused before that link is recorded. Routine output lists variable names, without values. Keep credentials out of flags and config contents; use the existing authentication channels. The record describes requested/staged inputs; confirm that a plugin consumed them in its own log or assertion.

`server-load-phases` accepts repeated `--env` in each phase's `args`, so phases can choose different values. Its private copied phase plan retains those values. Experimental `detach server-load` currently refuses both `--config` and `--env`; use foreground `--hold` for these inputs.

**Machine and installs**

| Option | Taken by | Does |
|---|---|---|
| `--inventory FILE` | all four | Uses a file's environments instead of [this machine](../reference/game-inventory.md#this-machine-with-no-file) |
| `--game DIR` | start, build | The client install to use |
| `--client-env NAME` | all four | The client environment to use from the inventory |
| `--server DIR` | server-load, server-load-ab, build | The dedicated-server install to use |
| `--server-env NAME` | server-load, server-load-ab, build | The server environment to use from the inventory |
| `--client DIR` | server-load, server-load-ab | The clean client's install |
| `--client-architecture x64\|arm64` | start, server-load, server-load-ab | The macOS client slice ([Apple Silicon](../reference/platforms.md#native-apple-silicon-client)) |
| `--join HOST:PORT` | server-load, server-load-ab | The address a client on another machine joins |

**Loader and ValheimCLI**

| Option | Taken by | Does |
|---|---|---|
| `--loader-package FILE` | server-load, server-load-ab, build | A [reviewed loader](../reference/game-run-inputs.md#pin-a-bepinex-loader) for the server's profile |
| `--client-loader-package FILE` | all four | A reviewed loader for the client's profile |
| `--cli-manifest FILE --cli-files DIR` | start, server-load, server-load-ab | A ValheimCLI core and packs from one build |
| `--steam-runtime-package FILE` | server-load | A Mac server's [pinned SteamCMD libraries](#steam-runtime-capture) |

**Timing, logs and holds**

| Option | Taken by | Does |
|---|---|---|
| `--join-seconds 10..900` | start, server-load, server-load-ab | The deadline to enter the world. Default 180 |
| `--server-startup-seconds 1..3600` | server-load, server-load-ab | The deadline for the server to become ready. Default 300 |
| `--expected-log-error EXACT_HEADER --expected-log-reason REASON` | start, server-load, server-load-ab | Admits one known BepInEx error by its whole header line, with a reason |
| `--hold` | start, server-load | Keeps a passing run's game open ([hold](#hold-and-finish)) |

## start

```sh
valheim-test start --mod /path/to/MyMod.dll
```

`start` loads a client-side mod in an owned Windows or macOS client that hosts the packaged world with the packaged character. It prints `run ID:`, a log-scan line and a `PASS` or `FAIL` result line.

For a control containing only the selected ValheimCLI plugin and required loader:

```sh
valheim-test start --no-mod --world-fixture /path/to/disposable-fixture --hold
```

`--no-mod` is explicit inside and outside a mod project. Without it, omitting `--mod` keeps implicit project selection: an absent, ambiguous, missing or stale build still refuses. A failed mod selection never becomes a control run. The control uses the same owned profile, world/character admission, strict CLI pins, hold, finish and cleanup as a modded start. Installed plugins and patchers are not inherited.

Do not combine `--no-mod` with `--mod`, `--project`, `--build-inputs`, `--source`, `--compare-mod`, `--compare-source`, `--dependency`, `--search-root`, `--optional-reference`, `--scenario` or `--scenario-project`. It is not an option of build, server-load, comparisons, phases or detach. Explicit `--config` and `--env` keep their admission and retained-byte rules; they select no plugin payload. CLI, loader, host or fixture failures still refuse the control.

Its inputs record `noMod: true`, an empty `mod` and one execution arm `control`. `evidence/control/run-manifest.json` records `noMod: true`, null mod identity fields and an empty mod-artifact arm list; the allowlist and strict pins still identify the real CLI. Result provenance says `noMod=true`, with no mod plugin/commit/hash. Use a matching toolkit reader for this additive input/result contract; older input readers reject `noMod`. Modded runs omit the false marker and keep their existing JSON. The generated `init` example targets a modded smoke arm; no control-specific consumer is generated.

| Option | Does |
|---|---|
| `--world-fixture DIR` | Hosts this world instead of the packaged one: a folder [`save-world`](#save-world) or a [bake](../reference/fixture-authoring.md) wrote, or any one complete Valheim 1.0 world, directly or under `worlds_local/`. It is checked before anything is built or staged, and against its `fixture-manifest.json` when it has one |
| `--source COMMIT` | Records the mod build's source commit |
| `--compare-mod DLL --compare-source COMMIT` | Runs a second build of the first mod against the same world, as arms `before` and `after` |

Both compared builds must declare the same plugin identities. The client needs its desktop session and a running Steam client. Through SSH on Windows, `start` launches the client in the signed-in desktop through a scheduled task; on macOS, run it from the unlocked console.

## server-load

```sh
valheim-test server-load --mod /path/to/ServerMod.dll
```

`server-load` prints the server and client it chose, retains any explicit config inputs, writes `campaign.json` and runs the host preflight before runtime staging (the preflight writes only a Linux or Mac server's trial link, made and removed under the runtime root). The preflight refuses a server and client whose installs are on different Valheim network versions. Then one clean client without the mod joins the owned server.

| Option | Does |
|---|---|
| `--server-only` | Runs no client. Prints `SERVER_LOAD_PASS` instead of `SERVER_JOIN_PASS` |
| `--preflight-only` | Stops after `PREFLIGHT PASSED` |
| `--plugin-file FILE`, `--plugin-dir DIR` | A mod's assets beside its DLL; DLLs are refused. Repeat |
| `--adapter DLL` | A built test adapter, instead of building the bundled one |
| `--adapter-project ADAPTER.csproj` | Builds a mod's own adapter; needs `--session-capability` and `--session-token-variable` |
| `--adapter-property NAME={mod}\|{cli}` | Maps an adapter project property to the pinned mod or ValheimCLI DLL. Repeat |
| `--session-capability OWNER/session --session-token-variable NAME` | The custom adapter's capability and token variable |
| `--world-fixture DIR` | Loads a pinned world fixture instead of the packaged world |
| `--bake-fixture NEW_DIR` | Saves the world as a new fixture; see [Bake a fixture](../reference/fixture-authoring.md) |
| `--assert-command TEXT --assert-line PREFIX` | One server command whose reply proves the mod is ready; needs `--server-only` |
| `--before-save-command TEXT --before-save-line PREFIX` | One change before a bake saves |

The result line is `SERVER_JOIN_PASS`, `SERVER_LOAD_PASS` or `SERVER_LOAD_FAIL`, with the evidence's size and path. The server must be on this machine. Building the bundled adapter needs NuGet.org; offline, pass `--adapter DLL`.

## server-load-ab

```sh
valheim-test server-load-ab \
  --mod /path/to/FirstMod.dll --mod /path/to/SecondMod.dll \
  --remove-mod /path/to/SecondMod.dll \
  --output /path/to/new-run
```

`--remove-mod DLL` names one of at least two `--mod` DLLs; `--output` is required. It refuses `--hold`, `--preflight-only`, `--project`, `--build-inputs`, `--dependency`, `--scenario`, `--scenario-project`, `--steam-runtime-package`, `--world-fixture` and the adapter-project, bake and assertion options. The full set runs in `before/` and the set without that mod in `after/`. The result line is `SERVER_MODSET_AB_PASS` or `SERVER_MODSET_AB_FAIL`.

## server-load-phases

```sh
valheim-test server-load-phases --plan FILE --output NEW_DIR
```

Both paths must be absolute. The plan and its output are on [Bake a fixture](../reference/fixture-authoring.md#ordered-phases).

## build

```sh
valheim-test build --project /path/to/MyMod/MyMod.csproj
```

`build` publicizes the selected install's assemblies into a new private folder and builds the mod against them, never into a game.

| Option | Does |
|---|---|
| `--role client\|server` | Builds against the client (default, as `start` selects it) or the dedicated server (as `server-load` selects it) |

With `--role server` it takes `--server`, `--server-env` and `--loader-package`; otherwise `--game`, `--client-env` and `--client-loader-package`. It prints `Built mod:` and `Build inputs:`. Pass both to a run with `--mod DLL --build-inputs FILE`.

It sets `VALHEIM_INSTALL`, `VALHEIM_MANAGED`, `ValheimPath`, `ValheimManaged`, `BEPINEX_CORE`, `BepInExCore`, `PUBLICIZED_PATH`, `PublicizedAssembliesPath` and `CopyOutputDLLPath` (a private folder), and empties `CopyOutputDLLPath2` and `CopyOutputDLLPath3`. Guard any other copy into a game first. Give a missing third-party reference with `--dependency` or `--search-root`.

## Your own scenario

A mod's test project can hold one public scenario that runs on the prepared game:

| Interface | Runs on | Example |
|---|---|---|
| `IOneShotServerScenario` | `server-load`; `RequiresClient` false needs `--server-only` | [MetadataScenario.cs](../../examples/OneShotServerScenario/MetadataScenario.cs) |
| `IOneShotHostedScenario` | `start` | [HostedWorldScenario.cs](../../examples/OneShotServerScenario/HostedWorldScenario.cs) |
| `IOneShotScenarioPreflight` | Either; `ValidateInputs()` runs before anything is staged | `context.RequireInputs<T>()` reads its result |

One command builds the mod, the scenario and its adapter, then runs them:

```sh
valheim-test server-load --server-only \
  --project /path/to/MyMod/MyMod.csproj \
  --scenario-project /path/to/MyMod.SystemTests/MyMod.SystemTests.csproj \
  --adapter-project /path/to/MyMod.TestAdapter/MyMod.TestAdapter.csproj \
  --adapter-property 'MyModDll={mod}' \
  --session-capability mymod.testing/session \
  --session-token-variable MYMOD_TEST_SESSION_TOKEN
```

A scenario owns its assertions, so it refuses `--hold`, `--bake-fixture` and `--assert-command`.

## init

```sh
valheim-test init --output NEW_DIR
valheim-test init server --output NEW_DIR
```

`init` creates an editable consumer project for a `start` run's `regression.json`, or with `server` for a `server-load` run's `campaign.json`. It pins the `Valheim.Testing.Game` this tool carries, and with `server` also `Valheim.Testing.GameSessions`, and restores from NuGet.org. A tool built from source refuses, because NuGet.org does not serve its version; `--candidate-feed LOCAL_PACKAGE_DIR` is for maintainers ([CONTRIBUTING](../../CONTRIBUTING.md)).

## hold and finish

```sh
valheim-test server-load --mod /path/to/ServerMod.dll --hold
valheim-test finish --run ID
```

`--hold` keeps the owned game running after the checks pass; a failing run is never held. It prints `HELD run` with the run ID and the `cli` commands that reach the game. `finish --run ID`, or Ctrl+C on the runner, stops and cleans up. If a held game exits, the run fails.

## save-world

```sh
valheim-test start --mod /path/to/MyMod.dll --hold
valheim-test save-world --run ID --output /path/to/saved-world
valheim-test start --mod /path/to/MyMod.dll --world-fixture /path/to/saved-world --hold
```

`save-world` keeps what a held `start` run generated in play, so a later run starts from it instead of generating it again. Give the run ID that `HELD run` printed and a new folder. The run must be held on this machine with its client in its world.

1. It finds the world the run placed in the client's `worlds_local`, from this machine's run journal.
2. It asks the game to save once (`valheim.session/save`) through the client's strict world pins, and waits up to two minutes for the game to confirm a completed save of that world.
3. It copies the newest save, the four `_main.<n>` files and the chunk files that save lists, into `NEW_DIR/worlds_local/<world>/`. Older chunk files and the minimap cache stay behind. Each file is hashed as it is read and read again afterwards; a save the game replaced meanwhile refuses the copy.
4. It writes `fixture-manifest.json`: the world's name, seed and UID, every file's SHA-256, and `savedFrom` (the run ID, the confirmed and copied save numbers, the time, the client's world pins, and from the run's `regression.json` the mod arms with their SHA-256 and `--source` commit, the other plugins, the ValheimCLI core and the world the run started from).

The folder appears complete or not at all. The run stays held: nothing else of it changes, and `finish` cleans it up as usual. The world keeps its UID and the run stages the packaged character fresh, so the character arrives where it does in the packaged world, not where the held run left it. A server's world is not saved this way: bake it with [`server-load --bake-fixture`](../reference/fixture-authoring.md).

## cli

```sh
valheim-test cli --evidence RUN_EVIDENCE --phase world --command TEXT
```


| Option | Does |
|---|---|
| `--evidence RUN_EVIDENCE` | The evidence folder holding the role's lease |
| `--role client\|server` | Needed only when the folder holds both leases |
| `--phase menu\|world` | Which published pins to verify first |
| `--expect-strict FILE` | Other strict pins, in place of `--phase` |
| `--command TEXT` | One game command; `cli_screenshot` takes only a size, 1 to 4 |
| `--timeout-seconds 1..15` | The command deadline. Default 5 |

| Run | Evidence folder |
|---|---|
| `start` | `RUN/evidence/smoke` |
| `server-load`, the server | `RUN/evidence` |
| `server-load`, the joined client | `RUN/evidence/client` |

It checks the exact process, the port's owner and the strict pins, records the command in `owned-cli-command-*.jsonl`, sends it once and never retries.

## env

| Command | Does |
|---|---|
| `env list [--inventory FILE] [--json]` | Prints each environment, `detected:` and `NOT FOUND:` lines. No verdict |
| `env preflight [--inventory FILE] [--json]` | Checks this machine before a one-off run. Prints `ELIGIBLE` or `REFUSED` |
| `env status [--inventory FILE] [--json]` | Lists what earlier runs left, from their journals. Changes nothing |
| `env recover --run ID [--inventory FILE] [--json]` | Removes what it can prove the run left, except what it kept on purpose |
| `env recover --run ID --launch-gone` | Settles a run whose only unprovable items are launches you confirmed are gone |
| `env teardown --run ID [--inventory FILE] [--json]` | As `recover`, and also removes what the run kept on purpose |
| `env teardown --copy PATH [--inventory FILE]` | Removes one copy made before runs journalled their copies |
| `env teardown --run ID --machine-gone [--inventory FILE]` | Releases the Steam leases of a run whose machine is gone for good |

`env preflight` checks this machine only ([its refusals](../reference/game-inventory.md#env-list-and-env-preflight)). `env status` prints each run that left something with its state:

| State | Means |
|---|---|
| `LIVE` | Its runner still runs; nothing is touched |
| `UNKNOWN` | It never journalled its end and its runner cannot be checked from here |
| `RECOVERABLE` | It left items that are provably its own; `env recover --run ID` |
| `UNRECOVERABLE` | Something cannot be proven gone or its own |
| `KEPT` | It ended and kept a runtime or lease on purpose; `env teardown --run ID` |

The last line is `N runs ended and left nothing. Nothing was changed.` `env status` exits 0 only when every run ended and left nothing; a `KEPT` run, an unjournalled copy or an unreadable host exits 3. `env recover` that keeps a runtime on purpose names `env teardown --run ID` and exits 3. [Debug a native run](../guides/debug-a-native-run.md#clean-up-what-a-run-left) says when to use each.

## session check

```sh
valheim-test session check SESSION [--hosts] [--json]
```

`session check` reports every problem with a `session.json`'s inputs and actor assignment without contacting a host, then `ELIGIBLE` or `REFUSED` lines. `--hosts` adds the read-only host checks (installs and their Valheim network versions, loaders, ports, free space, conflicting games, journals, signed-in Steam accounts) and prints `READY` when they pass. Sessions: [GameSessions](../reference/sessions-inventory.md).

## detach

```powershell
valheim-test detach server-load --server 'C:\Games\Valheim dedicated server' --mod 'C:\Mods\MyMod.dll' --server-only --output 'C:\ValheimTesting\runs\inspection-1'
valheim-test detach status
$evidence = 'C:\ValheimTesting\runs\inspection-1\evidence' # use the printed evidence path
valheim-test cli --evidence $evidence --role server --phase world --command cli_manifest
valheim-test detach save --evidence $evidence
valheim-test detach stop --evidence $evidence
valheim-test detach status --evidence $evidence
valheim-test detach restart --evidence $evidence
valheim-test detach finish --run ID
```

Experimental, Windows only. `detach server-load` starts a server-only held run under Task Scheduler and returns once it is held; it needs a signed-in desktop session and absolute paths. `detach status` exits 0 when nothing is left to recover, a live run included. Recover an interrupted start with `env recover --run ID` on the same machine.

Use the evidence directory printed by the held server for later `cli` calls. Detached evidence routes them through the original supervisor: the caller proves its journalled identity, and the supervisor checks the exact server boot, listener owner and strict pins before dispatch. The supervisor alone appends command intent and outcome to the run journal. `finish` closes command admission, waits for the current operation to settle, and refuses work still queued before teardown.

Each supervisor lifetime accepts at most 64 write-once requests, commands and checkpoints together. Restart never recreates the queue or reuses slots. A request expires if it has not started within 60 seconds; each game call keeps the existing `--timeout-seconds 1..15` bound. Private evidence records each operation ID and its `completed`, `refused` or `unknown` outcome. A lost reply or interrupted dispatch is `unknown`: never repeat that mutation; inspect the evidence and use read-only observations to establish the state. The route stays in evidence after finish, so a later call refuses instead of contacting a leftover server directly. An unknown operation blocks all later game commands and checkpoints; use status and evidence, then finish or exact recovery. This remains Windows server-only; detached clients and detached macOS ownership are separate work.

`save` requires the pinned world's completed-save result with an advancing save number. `stop` first confirms a fresh save, asks only the exact owned process to quit, then archives and hashes its world and stopped boot logs. A forced kill or prior exit fails the clean checkpoint. In `stopped`, the supervisor retains the host lock, profile and original fixture; ordinary game commands are refused. `restart` verifies the archive and source hashes, restores the immutable original fixture (discarding the current round only after its verified archive), and boots through the same owner. It verifies the new session identity, strict pins, test access and world readiness before publishing the next boot. Requests naming the previous boot are refused even if they were queued before restart. A live waiter follows each boot; an unexpected exit closes admission and marks the state unknown. Final `finish` uses the normal verified-fetch retirement and releases the run's ownership.

Use an absolute `--world-fixture DIR` on `detach server-load` to select a fixture. Before Task Scheduler starts the supervisor, the starter stages that directory and the selected mod inputs and records both source and staged SHA-256 manifests. The supervisor rechecks them before startup and checkpoints. Keep those inputs unchanged for the run's lifetime. Output and staging directories must be outside every original input directory, including through links. Without this option, the packaged smoke fixture remains the original. Each new boot uses the same pinned build unless the run accepts its one explicit stopped build swap.

`detach status --evidence DIR` reports the current boot and `running`, `saving`, `stopping`, `stopped`, `swapping`, `starting` or `unknown` state. Checkpoints have a cancellation deadline of at least 15 minutes, extended to cover two configured startup waits plus five minutes. The caller's wait also covers a save or stop already in progress and the stop's bounded log fetch, plus a transport margin. These completion waits are separate from the 60-second admission expiry. A missing reply is never permission to retry. Private `checkpoints/` archives, manifests and per-operation records remain in the evidence output.

While the server is clean-stopped, one `swap` can replace the first selected mod DLL:

```powershell
valheim-test detach swap --evidence $evidence --mod 'C:\Builds\next\MyMod.dll' --sha256 REVIEWED_SHA256 --build-inputs 'C:\Builds\next\build-inputs.json' --dependencies 'C:\Builds\next\dependencies.lock.json'
valheim-test detach restart --evidence $evidence
valheim-test cli --evidence $evidence --role server --phase world --command cli_manifest
```

Supply the full SHA-256 reviewed for the new DLL, its `valheim-test build` input manifest, and a ready dependency lock selecting that DLL first. The build must keep the original single plugin GUID, compile game and loader, companion mods, resolved dependencies, optional-reference decisions and ValheimCLI bytes. Config, assets and the original fixture stay fixed. Source files must be regular files under unlinked directories, outside the owned profile and evidence; input directories are limited to 10,000 entries and 512 MiB. Keep the original and replacement source inputs unchanged until finish.

The supervisor journals intent before staging and activation, verifies source and staged bytes, and replaces the selected profile DLL atomically while stopped. Its existing profile checks and owned session's strict MD5 expectation then transition together. `swap` does not boot the server or publish new command pins; `restart` restores the original fixture and publishes the new boot only after strict startup verification. Private evidence retains the original dependency provenance, replacement build inputs and source hashes; the run journal records the source-to-staged target mapping. An interrupted activation has an unknown outcome and cannot be replayed. Finish or exact recovery removes partial staging with the existing owned profile.

## steam-runtime capture

```sh
valheim-test steam-runtime capture --steamcmd DIR --output NEW_FILE
```

macOS only. It writes a reviewed manifest of SteamCMD's native server libraries. Pass it to `server-load` as `--steam-runtime-package FILE` for a Mac dedicated server profile ([Steam runtime package](../reference/platforms.md#steam-runtime-package)).

## What every run does

| Behaviour | Rule |
|---|---|
| Profiles | Each actor runs from a disposable loader profile; the install is only read ([profiles](../reference/sessions-profiles.md)) |
| Loader | Windows and Linux: the pinned BepInExPack_Valheim from [loader-dependency.json](../../loader-dependency.json), or a named loader package; never the install's. macOS: its loader package, or the install's native loader |
| ValheimCLI | `--cli-manifest` and `--cli-files`, else `VALHEIMCLI_BUNDLE`, else the bundle pinned in the tool. The run prints which |
| Output | Must be new and outside every install and the mod project; a refusal writes `REFUSED.txt` with the reason |
| Journal | Every copy, character, process, lease and lock is journalled before it is made, for `env status` |
| User files | The client's recent-servers list is moved aside before launch and put back after |
| Log scan | Logs are scanned at teardown ([log scan](../reference/game-log-scan.md)); one summary line per arm |

| Environment variable | Does |
|---|---|
| `VALHEIMCLI_BUNDLE` | A folder with one ValheimCLI manifest and its DLLs |
| `VALHEIM_TESTING_KEEP_RUNTIME=1` | Keeps the owned runtime; `env status` then reports the run as `KEPT` |
| `VALHEIM_TESTING_SOURCE_HASH_AFTER_RUN=1` | Re-hashes the whole source install after the run, not only sizes and times ([source checks](../reference/sessions-profiles.md#source-checks)) |

## What a run writes

| File | Holds |
|---|---|
| `dependencies.lock.json` | Every resolved file and dependency decision |
| `campaign.json`, `plan.json`, `client-plan.json` | `server-load`'s derived inputs; `plan.json` holds the server password |
| `regression.json`, `environments.json` | `start`'s derived inputs |
| `evidence/` | `result.json`, `junit.xml` and the kept logs ([where each log is](../guides/debug-a-native-run.md#read-the-kept-logs)); `source-changes/<actor>/` when the game wrote into an install ([what the game wrote](../guides/debug-a-native-run.md#read-what-the-game-wrote-into-your-install)) |

`result.json` reports each step with its phase, four states and the `Toolkit` that ran ([evidence](../reference/evidence.md)). Keep the output private: it holds local paths, a server password, account identifiers and a copy of your recent-servers list.

## Limits

- A pass proves the plugins loaded and the game entered a world, not the mod's gameplay.
- `server-load` keeps its server on this machine. A server on another machine runs through an [inventory file](../reference/sessions-inventory.md) ([run on another machine](../guides/run-on-another-machine.md)).
- `cli` reaches only games a run on this machine owns.
- The packaged world and character come from one game version; recheck them after a game update. Run one client with the packaged character at a time.
