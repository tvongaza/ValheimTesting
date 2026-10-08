# Developer loop

[`dev-loop.cs`](dev-loop.cs) runs one round of a mod's edit-build-test loop against a game copy you own: build the mod, install its DLL into `BepInEx/plugins`, launch the game, run a test plan in strict mode, then count the log's warnings and errors. [`smoke-plan.yaml`](smoke-plan.yaml) is a strict plan to start from. The script drives the `valheim-cli` executable; it is not part of the libraries, and `scripts/validate.cs` runs it in-process against a fake tool runner, and once as shipped to see it start (see [Tests](#tests)).

It is a .NET file-based script, one file for every OS, and needs a .NET 10 SDK. A `global.json` that pins an older SDK where you run it stops `dotnet` before the script starts; run it from a folder without that pin, with full paths. From your mod's checkout, with the paths prefixed by wherever your ValheimTesting checkout is:

```sh
VALHEIM_PATH=/path/to/owned/Valheim VALHEIM_CLI=/path/to/valheim-cli VALHEIM_EXPECTATIONS=pins.txt \
  dotnet run --file tools/dev-loop/dev-loop.cs -- MyMod.csproj tools/dev-loop/smoke-plan.yaml
```

```powershell
$env:VALHEIM_PATH = 'D:\valheim-copies\Valheim'
$env:VALHEIM_CLI = 'C:\path\to\valheim-cli.exe'
$env:VALHEIM_EXPECTATIONS = "$PWD\pins.txt"
dotnet run --file tools\dev-loop\dev-loop.cs -- "$PWD\MyMod.csproj" "$PWD\tools\dev-loop\smoke-plan.yaml"
```

`--file` matters in a folder that holds a project: without it `dotnet run` runs that project instead. The SDK starts a file-based script in the script's own folder, not yours, so relative paths (the project, the plan, `VALHEIM_PATH`, `VALHEIM_EXPECTATIONS`, `VALHEIM_CLI`) are read from the folder your shell passes as `PWD`, and the tools run there. bash and zsh keep it current; PowerShell and cmd do not, so there give full paths as above, and the tools run in the project's folder. Without `PWD` the script refuses a relative path rather than read it from `tools/dev-loop`, and it refuses a project or plan that is not where it looked.

The script's header lists its environment variables, options and exit codes. Options: `--fail-on warning|error` fails a round whose plan passed when the log has a line at that level or worse; `--live` allows a Steam library install (below).

The dev-loop, pin-mods and log-summary scripts moved here from ValheimCLI (commit `ee4cd23`) on 28 September 2026 as bash and PowerShell twins; `dev-loop.cs` replaced the twins and `log-summary.sh` on 4 October 2026. `pin-mods`, `world-hash` and `sample-value` were removed then: each was a wrapper around a `valheim-cli` command or a recipe the transport package already owns (see [Pins without the dev loop](#pins-without-the-dev-loop)).

## Get valheim-cli

ValheimTesting does not build the executable. Take `valheim-cli` from a [ValheimCLI](https://github.com/tvongaza/valheimCLI) release, or build it from source:

```sh
git clone https://github.com/tvongaza/valheimCLI.git
cd valheimCLI/CLI
dotnet build -c Release     # CLI/bin/Release/net9.0/valheim-cli (valheim-cli.exe on Windows)
```

Use a build at or after the ValheimCLI commit pinned in [`cli-dependency.json`](../../cli-dependency.json): the script needs `--status` with its `local_process=` field, `--expect-strict`, `--test`, `--launch`, `--progress` and `--stall`. The game needs the matching `valheimCLI.dll` in `BepInEx/plugins`. Put `valheim-cli` on `PATH`, or point `VALHEIM_CLI` at it.

## Which game it installs into

`VALHEIM_PATH` is required and has no default: the script copies the build into that folder's `BepInEx/plugins` and `valheim-cli` launches the game from it. Point it at a copy of the game you own and can throw away, not at the install Steam updates and every other launch uses. A folder under a Steam library's `steamapps/common` is refused (exit 3) unless you pass `--live`, which says you mean to change that install. Build-time references find the game through [`tools/game-references`](../game-references/README.md), the one place that knows the Steam folders.

The game must not be running when the script deploys: a running game keeps the old build (and on Windows locks the file). The script refuses to deploy unless `valheim-cli --status` reports `local_process=false`.

## Strict pins in the dev loop

With a plan, the script runs it in strict mode against `VALHEIM_EXPECTATIONS`. Every rebuild changes the mod's md5, so it never passes that file as it is, and never accepts whatever md5 the game reports: it hashes the DLL it just built and deploys, writes a temporary copy of the pins file in which only that mod's pin is replaced, checks the installed copy matches, and passes the copy to `--expect-strict`. The pins file must pin the mod exactly once, under its DLL name or under the key named by `VALHEIM_PLUGIN_KEY` (its GUID or name); otherwise the script stops before deploying. Every other pin, including the core and pack hashes, is kept as written. See ValheimCLI's `docs/expectations.md` for the pins file format.

A launch without a plan, or `valheim-cli --status`, only brings a game up; neither is a test result.

## Pins without the dev loop

`valheim-cli` takes pins directly; there is no wrapper script:

| | Command |
| --- | --- |
| snapshot the running game's plugin pins (an explicit setup step: review the file before accepting it as a test's expectation) | `valheim-cli manifest --write pins.txt` (add `--with-world` for the loaded world) |
| check a game against them, strictly (exit 6 with the mismatches on drift) | `valheim-cli --expect-strict pins.txt` |
| run a console command only on a matching game | `valheim-cli --expect-strict pins.txt cli_manifest` |
| run a plan only on a matching game | `valheim-cli --expect-strict pins.txt --test plan.yaml` |

The load-time world files hash (`cli_world`'s `files=`, the `worldfiles` pin) is `Expectations.HashDirectory` in the `Valheim.Testing.Cli` package, documented once in ValheimCLI's [`docs/expectations.md`](https://github.com/tvongaza/valheimCLI/blob/9e8ca679298e559e995ab5b04ef84b782b15a0ad/docs/expectations.md#world-files-hash). To record a value over time, call `cli_call` from a test; to wait for a condition, use an event wait (see [Waiting](../../docs/packages/Valheim.Testing.Game.md#waiting)) or `valheim-cli wait --for`.

## The smoke plan

[`smoke-plan.yaml`](smoke-plan.yaml) checks that the game still reaches a playable world with the mod installed and that the mod answers: it waits for the main menu, makes a throwaway local character and world (so it never touches real saves), waits for the world, checks the player, turns on player safety, runs one of the mod's console commands and takes a screenshot. Replace the `mod_command` and `mod_answer` variables (or pass `--var mod_command=...`) with a command of your own and the text it prints.

The plan is strict by itself: `game.expect` names a pins file next to the plan and `game.expectStrict` is true, so `valheim-cli --test` checks every loaded plugin before the first step even without `--expect-strict`. The dev loop always passes `--expect-strict` with the pin of the build it just deployed, which overrides the plan's file. `SmokePlanTests` parses the plan with the transport package's own plan model and fails if it stops being strict.

## Waiting for a log line

There is no script for this; the libraries wait on events. In a test, `LogWait` follows a local log from its current end (or a byte offset), so earlier lines never match, and waits for a log that does not exist yet; give it every outcome, failures included, so a failure returns as fast as a success (see [Waiting](../../docs/packages/Valheim.Testing.Game.md#waiting)). For a game on another machine, a game host's `LogOffsetAsync` and `WaitForLogAsync` follow the log there (see [game hosts](../../docs/packages/Valheim.Testing.GameSessions.md#game-hosts-and-the-environment-inventory)). To wait for a game state rather than a line, use `StateWait` or `valheim-cli wait --for`.

## A game on another machine

ValheimCLI listens on its machine's loopback only. Reach it through an SSH port forward that is bound to loopback on both ends, then tell `valheim-cli` the game is remote so it reads no local process or log and never launches or stops anything:

```sh
ssh -N -L 127.0.0.1:5556:127.0.0.1:5555 user@game-host      # leave running; Ctrl-C closes it
valheim-cli --port 5556 --remote --status
valheim-cli --port 5556 --expect-strict pins.txt
```

The dev loop deploys to a local game folder, so it is for a local game only. A test drives remote games through a game host instead, whose `OpenCliTunnelAsync` opens the same loopback-only forward and refuses one that could listen beyond loopback (see [game hosts](../../docs/packages/Valheim.Testing.GameSessions.md#game-hosts-and-the-environment-inventory)).

## Tests

`tests/Valheim.Testing.Tests` compiles `dev-loop.cs` without its entry point and `DevLoopTests` runs it in-process on every OS against a fake tool runner and a temporary game folder; nothing is built, installed into a real game or launched. The cases: the stopped-game guard (a running game, a stopped one, no answer, conflicting answers, a `valheim-cli` that does not start), relative paths read from `PWD` with the tools run there, a relative path without `PWD` (the tools then run in the project's folder), a project or plan that is not there, no `VALHEIM_PATH`, a Steam library install with and without `--live` and one reached through a link, the pins derived from the build (other lines kept, the key named by `VALHEIM_PLUGIN_KEY`, no pin or two pins for the build), the build's and the plan's exit codes, the log summary and `--fail-on`, one real process for the tool runner itself, and one `dotnet run --file` of the script as shipped. `SmokePlanTests` parses the plan. `dotnet run scripts/validate.cs` runs them with the rest of the library tests.
