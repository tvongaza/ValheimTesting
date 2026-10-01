# Developer-loop scripts

Scripts for a mod's edit-build-test loop against a local game, and a plan to start from. They drive the `valheim-cli` executable; they are not part of the libraries and never run during `scripts/validate.cs` except under the inert-tool tests described below.

| Script | What it does |
|---|---|
| `dev-loop.sh` / `dev-loop.ps1` | Build a mod, install its DLL into `BepInEx/plugins`, launch Valheim and run a test plan in strict mode, then summarise the log |
| `pin-mods.sh` / `pin-mods.ps1` | Snapshot the plugin pins of a running game, check a game against them (strict), or run a command only on a matching game |
| `log-summary.sh` | Count the warnings and errors in `BepInEx/LogOutput.log` by level, source and message; `dev-loop.sh` runs it after every round |
| `smoke-plan.yaml` | A strict test plan for any mod: reach a throwaway local world with the mod loaded, check the player and one of the mod's commands, take a screenshot |
| `world-hash.sh` / `world-hash.ps1` | Hash a world's save folder the way the game does at load (`cli_world`'s `files=`, the `worldfiles` pin), and with `--compare` check it against the running game |
| `sample-value.sh` / `sample-value.ps1` | Sample one static member of the game or a mod with `cli_call` at a fixed interval and write the series as CSV |

The dev-loop, pin-mods and log-summary scripts moved here from ValheimCLI (commit `ee4cd23`) on 28 September 2026; `smoke-plan.yaml`, `world-hash.sh` and `sample-value.sh` followed from commit `d112140` on 30 September 2026, and `world-hash.ps1` and `sample-value.ps1` were added here as their Windows twins. ValheimCLI stays a minimal executable and game-side API, and the support scripts live with the testing toolkit. The moved scripts' behaviour did not change, except that `world-hash` now refuses an argument other than `--compare`.

## Get valheim-cli

ValheimTesting does not build the executable. Take `valheim-cli` from a [ValheimCLI](https://github.com/tvongaza/valheimCLI) release, or build it from source:

```sh
git clone https://github.com/tvongaza/valheimCLI.git
cd valheimCLI/CLI
dotnet build -c Release     # CLI/bin/Release/net9.0/valheim-cli (valheim-cli.exe on Windows)
```

Use a build at or after the ValheimCLI commit pinned in [`cli-dependency.json`](../../cli-dependency.json): the scripts need `--status` with its `local_process=` field, `--expect-strict`, `--test`, `--launch`, `--progress`, `--stall`, `--remote` and `manifest --write`. The game needs the matching `valheimCLI.dll` in `BepInEx/plugins`. Put `valheim-cli` on `PATH`, or point `VALHEIM_CLI` at it.

## bash or PowerShell

The dev-loop, pin-mods, world-hash and sample-value scripts come as twins with the same steps, environment variables and exit codes: bash for macOS and Linux, PowerShell for Windows (Windows PowerShell 5.1 or PowerShell 7). Keep the two in step when changing either. Run them from your mod's checkout:

| | macOS / Linux (bash) | Windows (PowerShell) |
| --- | --- | --- |
| build, deploy, run a plan | `tools/dev-loop/dev-loop.sh MyMod.csproj plan.yaml` | `powershell -ExecutionPolicy Bypass -File tools\dev-loop\dev-loop.ps1 MyMod.csproj plan.yaml` |
| snapshot pins | `tools/dev-loop/pin-mods.sh snapshot pins.txt` | `powershell -ExecutionPolicy Bypass -File tools\dev-loop\pin-mods.ps1 snapshot pins.txt` |
| check pins (strict) | `tools/dev-loop/pin-mods.sh check pins.txt` | `powershell -ExecutionPolicy Bypass -File tools\dev-loop\pin-mods.ps1 check pins.txt` |
| summarise the last run's log | `tools/dev-loop/log-summary.sh` | printed by `dev-loop.ps1` |
| hash a world, compare with the game | `tools/dev-loop/world-hash.sh Dev --compare` | `powershell -ExecutionPolicy Bypass -File tools\dev-loop\world-hash.ps1 Dev --compare` |
| sample a value over time | `tools/dev-loop/sample-value.sh EnvMan.IsDay 10 5 > day.csv` | `powershell -ExecutionPolicy Bypass -File tools\dev-loop\sample-value.ps1 EnvMan.IsDay 10 5 > day.csv` |

Prefix the paths with wherever your ValheimTesting checkout is. Set the environment in each shell's own way:

```bash
VALHEIM_CLI=/path/to/valheim-cli VALHEIM_EXPECTATIONS=pins.txt tools/dev-loop/dev-loop.sh MyMod.csproj tools/dev-loop/smoke-plan.yaml
```

```powershell
$env:VALHEIM_CLI = 'C:\path\to\valheim-cli.exe'
$env:VALHEIM_EXPECTATIONS = 'pins.txt'
powershell -ExecutionPolicy Bypass -File tools\dev-loop\dev-loop.ps1 MyMod.csproj tools\dev-loop\smoke-plan.yaml
```

[`smoke-plan.yaml`](smoke-plan.yaml) is a plan to start from; see [the smoke plan](#the-smoke-plan). `VALHEIM_PATH` defaults to the Steam folder of each system; on Windows that is `C:\Program Files (x86)\Steam\steamapps\common\Valheim`. Where `dev-loop.sh` ends with `log-summary.sh`, `dev-loop.ps1` prints the log's warning, error and fatal counts by level and source. Each script's header lists its environment variables and exit codes.

The game must not be running when `dev-loop` deploys: a running game keeps the old build (and on Windows locks the file). The script refuses to deploy unless `valheim-cli --status` reports `local_process=false`.

## Strict pins in the dev loop

`dev-loop <MyMod.csproj> <plan.yaml>` runs the plan in strict mode against `VALHEIM_EXPECTATIONS`. Every rebuild changes the mod's md5, so the script never passes that file as it is, and never accepts whatever md5 the game reports: it hashes the DLL it just built and deploys, writes a temporary copy of the pins file in which only that mod's pin is replaced, checks the installed copy matches, and passes the copy to `--expect-strict`. The pins file must pin the mod exactly once, under its DLL name or under the key named by `VALHEIM_PLUGIN_KEY` (its GUID or name); otherwise the script stops before deploying. Every other pin, including the core and pack hashes, is kept as written. See ValheimCLI's `docs/expectations.md` for the pins file format.

`pin-mods check` and `run` are strict by default, like `dev-loop`. `pin-mods snapshot` is an explicit read-only setup step: it writes down whatever the game runs now, so review the file before accepting it as a test's expectation. A launch without a plan, or `valheim-cli --status`, only brings a game up; neither is a test result.

## The smoke plan

[`smoke-plan.yaml`](smoke-plan.yaml) checks that the game still reaches a playable world with the mod installed and that the mod answers: it waits for the main menu, makes a throwaway local character and world (so it never touches real saves), waits for the world, checks the player, turns on player safety, runs one of the mod's console commands and takes a screenshot. Replace the `mod_command` and `mod_answer` variables (or pass `--var mod_command=...`) with a command of your own and the text it prints.

The plan is strict by itself: `game.expect` names a pins file next to the plan and `game.expectStrict` is true, so `valheim-cli --test` checks every loaded plugin before the first step even without `--expect-strict`. `dev-loop` always passes `--expect-strict` with the pin of the build it just deployed, which overrides the plan's file. `SmokePlanTests` parses the plan with the transport package's own plan model and fails if it stops being strict.

## World files hash

`world-hash.sh <world>` (or `world-hash.ps1`) prints the hash the game takes of a world's save folder just before loading it: `cli_world`'s `files=` field and the `worldfiles` pin. The world is a name under `VALHEIM_SAVES` (default: the game's `worlds_local` folder) or a folder path. With `--compare` it also asks the running server or host (`cli_world`) and exits 1 when the two differ, or when the game has no load-time hash (a joined client, or no world loaded). Use it on a fixture copy you restore before each run: hash the copy, then compare after the game has loaded it. The recipe is documented once, in ValheimCLI's [`docs/expectations.md`](https://github.com/tvongaza/valheimCLI/blob/review/cli-command-packs-ready/docs/expectations.md#world-files-hash); the tests check both scripts against a hand-worked vector with nested files and against the game's own implementation, which the transport package carries.

## Sampling a value over time

`sample-value.sh <[Namespace.]Type.Member> <count> <interval-seconds> [arg ...]` (or `sample-value.ps1`) calls one static member with `cli_call` every interval and writes CSV with the columns `sample,utc,value,error`. `value` is the `VALUE` text as `cli_call` prints it, or the item count for a collection; `error` is the first line of a failed reply. A failed first sample stops the script (the member or its arguments are wrong); a later failure is recorded as a row and sampling goes on. The interval sets a time series' rate; it is not a wait for a condition, which is a job for an event wait (see [Waiting](../../docs/testing-toolkit.md#waiting)) or `valheim-cli wait --for`. `cli_call` needs ValheimCLI's Reflection pack and devcommands.

## Waiting for a log line

There is no shell script for this; the libraries wait on events. In a test, `LogWait` follows a local log from its current end (or a byte offset), so earlier lines never match, and waits for a log that does not exist yet; give it every outcome, failures included, so a failure returns as fast as a success (see [Waiting](../../docs/testing-toolkit.md#waiting)). For a game on another machine, a game host's `LogOffsetAsync` and `WaitForLogAsync` follow the log there (see [game hosts](../../docs/testing-toolkit.md#game-hosts-and-environment-profiles-preview-13)). To wait for a game state rather than a line, use `StateWait` or `valheim-cli wait --for`.

## A game on another machine

ValheimCLI listens on its machine's loopback only. Reach it through an SSH port forward that is bound to loopback on both ends, then tell `valheim-cli` the game is remote so it reads no local process or log and never launches or stops anything:

```sh
ssh -N -L 127.0.0.1:5556:127.0.0.1:5555 user@game-host      # leave running; Ctrl-C closes it
valheim-cli --port 5556 --remote --status
VALHEIM_CLI_PORT=5556 tools/dev-loop/pin-mods.sh check pins.txt
```

`dev-loop` deploys to a local game folder, so it is for a local game only. `pin-mods` and `sample-value` work through the tunnel with `VALHEIM_CLI_PORT`, and so does `world-hash --compare` when the folder it hashes is a copy of the one the remote game loaded. A test drives remote games through a game host instead, whose `OpenCliTunnelAsync` opens the same loopback-only forward and refuses one that could listen beyond loopback (see [game hosts](../../docs/testing-toolkit.md#game-hosts-and-environment-profiles-preview-13)).

## Tests

`tests/Valheim.Testing.Tests` runs the real scripts against inert fake tools in a temporary directory; nothing is built, installed or launched. `DevLoopGuardTests` and `StrictExampleTests` run the bash scripts on macOS and Linux and are reported as skipped on Windows. `DevLoopGuardPowerShellTests` and `StrictExamplePowerShellTests` run the same cases against the PowerShell twins under Windows PowerShell on Windows and are reported as skipped elsewhere. `WorldHashScriptTests` and `SampleValueScriptTests` hold each case once and run it against `world-hash.sh`/`sample-value.sh` on macOS and Linux and against the `.ps1` twins on Windows: the world hash of a fixture folder with nested files against a hand-worked vector and the game's own implementation, by folder and by world name, and `--compare` against a fake `cli_world` that agrees, disagrees, is a client without files or cannot connect; the CSV columns, a failed first sample that stops the run, and a later failure that is recorded while sampling goes on. `SmokePlanTests` runs on every OS. `dotnet run scripts/validate.cs` runs them with the rest of the library tests.
