# Developer loop

Build your mod, install it into a game copy, launch the game and run a strict test plan, all in one command.

[`dev-loop.cs`](dev-loop.cs) runs one round of a mod's edit-build-test loop against a game copy you own. [`smoke-plan.yaml`](smoke-plan.yaml) is a strict plan to start from. The script drives the `valheim-cli` executable. It is not part of the libraries.

## What one round does

1. Refuses to run unless two checks pass. `VALHEIM_PATH` must name a game folder with `BepInEx/plugins` that is not a Steam library install. `valheim-cli --status` must report `local_process=false`.
2. Runs `dotnet build -c Release`. It stops if the build fails.
3. With a plan, writes a temporary copy of `VALHEIM_EXPECTATIONS`. In that copy, your mod's pin is the md5 of the DLL it just built.
4. Copies the built DLL and its `.pdb` into `BepInEx/plugins`. Then it checks that the installed copy is the build.
5. Launches the game and runs the plan with `valheim-cli --expect-strict <derived pins> --test ... --launch`. Without a plan, it launches and waits until the console is ready.
6. Counts the warnings and errors in `BepInEx/LogOutput.log` by level and source. It also lists the most frequent messages.

## Run it

You need a .NET 10 SDK and `valheim-cli` (see [Get valheim-cli](#get-valheim-cli)). If a `global.json` pins an older SDK where you run the script, `dotnet` stops before the script starts. Run it from a folder without that pin, with full paths.

Run these from your mod's checkout. Prefix the paths with the location of your ValheimTesting checkout.

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

The script prints a `==` line for each stage: build, pins, install, launch and log. It ends with a table of counts by level and source and a `total: fatal=... error=... warning=...` line. Last come the most frequent messages, with numbers shown as `#`.
<!-- TODO(docs): capture output -->

### Paths and `--file`

- Keep `--file`. In a folder that holds a project, `dotnet run` without it runs that project instead.
- The SDK starts the script in the script's own folder. The script reads relative paths from the folder your shell passes as `PWD`, and runs the tools there. This covers the project, the plan, `VALHEIM_PATH`, `VALHEIM_EXPECTATIONS` and `VALHEIM_CLI`.
- bash and zsh keep `PWD` current. PowerShell and cmd do not, so give full paths there. The tools then run in the project's folder.
- Without `PWD`, the script refuses a relative path. It also refuses a project or plan that is not where it looked.

## Options

| Option | Effect |
| --- | --- |
| `<MyMod.csproj>` | Required. The mod project to build. |
| `[test-plan.yaml]` | Optional. The plan to run in strict mode. |
| `--fail-on warning\|error` | Fails a passing round if the log has a line at that level or worse. |
| `--live` | Allows a Steam library install as `VALHEIM_PATH`. |

## Environment variables

| Variable | Default | Meaning |
| --- | --- | --- |
| `VALHEIM_PATH` | none, required | The game folder that contains BepInEx: a copy you own. |
| `VALHEIM_CLI` | `valheim-cli` on `PATH` | The `valheim-cli` executable. |
| `VALHEIM_EXPECTATIONS` | none, required with a plan | The pins file. It must pin your mod exactly once. |
| `VALHEIM_PLUGIN_KEY` | built DLL's name without `.dll` | The key that pins your mod: its GUID, name or DLL name. |
| `VALHEIM_CLI_PORT` | `5555` | The valheimCLI port. |
| `CONFIGURATION` | `Release` | The build configuration. |
| `STOP_AFTER` | none | `1` quits the game after a plan that passed. |
| `PROGRESS` | `valheim-cli`'s `15s` | The heartbeat interval, for example `30s`. `0` disables it. |
| `STALL` | `valheim-cli`'s `120s` | Ends a plan's wait after this long without change. `0` disables it. |

## Exit codes

| Code | Meaning |
| --- | --- |
| the build's | The build failed. |
| the plan's, or the launch's | Every other round that gets past the build. |
| `1` | No built or installed DLL, `--fail-on` found a line, or a copy, hash or tool failed. |
| `3` | No `VALHEIM_PATH`, a Steam install without `--live`, no `BepInEx/plugins`, or no log for `--fail-on`. |
| `4` | Usage error, a missing project, plan or pins file, a bad pin, or a relative path without `PWD`. |
| `5` | The game is running, or its state is unknown. |

## Get valheim-cli

ValheimTesting does not build the executable. Take `valheim-cli` from a [ValheimCLI](https://github.com/tvongaza/valheimCLI) release, or build it from source:

```sh
git clone https://github.com/tvongaza/valheimCLI.git
cd valheimCLI/CLI
dotnet build -c Release
```

The build writes `CLI/bin/Release/net9.0/valheim-cli`, or `valheim-cli.exe` on Windows.

Use a build at or after the ValheimCLI commit pinned in `cli-dependency.json` at the repository root. The script needs these options:

- `--status` with its `local_process=` field
- `--expect-strict`
- `--test`
- `--launch`
- `--progress`
- `--stall`

The game needs the matching `valheimCLI.dll` in `BepInEx/plugins`. Put `valheim-cli` on `PATH`, or point `VALHEIM_CLI` at it.

## Which game it installs into

`VALHEIM_PATH` has no default. The script copies the build into that folder's `BepInEx/plugins`, and `valheim-cli` launches the game from there. Point it at a copy you own and can throw away, not at the install Steam updates.

The script refuses a folder under a Steam library's `steamapps/common` (exit 3) unless you pass `--live`. The check also follows links and junctions.

The game must not be running when the script deploys. A running game keeps the old build, and on Windows it locks the file.

Build-time references find the game through [`tools/game-references`](../game-references/README.md).

## Strict pins in the dev loop

Every rebuild changes your mod's md5. For that reason, the script never passes `VALHEIM_EXPECTATIONS` as written, and it never accepts the md5 the game reports. It hashes the DLL it just built and replaces only your mod's pin in a temporary copy. Then it passes that copy to `--expect-strict`.

Every other pin stays as written, including the default ValheimCLI plugin hash. The pins file must pin your mod exactly once, under its DLL name or under `VALHEIM_PLUGIN_KEY`. If it does not, the script stops before deploying. For the file format, see ValheimCLI's `docs/expectations.md`.

## Pins without the dev loop

`valheim-cli` takes pins directly:

| Task | Command |
| --- | --- |
| Snapshot the running game's plugin pins. Review the file before you trust it. | `valheim-cli manifest --write pins.txt` (add `--with-world` for the loaded world) |
| Check a game strictly. Drift exits 6 and lists the mismatches. | `valheim-cli --expect-strict pins.txt` |
| Run a console command only on a matching game | `valheim-cli --expect-strict pins.txt cli_manifest` |
| Run a plan only on a matching game | `valheim-cli --expect-strict pins.txt --test plan.yaml` |

The load-time world files hash is `Expectations.HashDirectory` in the `Valheim.Testing.Cli` package. It appears as `cli_world`'s `files=` and as the `worldfiles` pin. See ValheimCLI's [`docs/expectations.md`](https://github.com/tvongaza/valheimCLI/blob/e596a7d984feaaa695f5c2999964b64e23fbfd92/docs/expectations.md#world-files-hash) for details.

- To record a value over time, call `cli_call` from a test.
- To wait for a condition, use an event wait ([Valheim.Testing.Game](../../docs/packages/Valheim.Testing.Game.md)) or `valheim-cli wait --for`.

## The smoke plan

[`smoke-plan.yaml`](smoke-plan.yaml) checks that the game still reaches a playable world with your mod installed, and that the mod answers. It does six things:

1. Waits for the main menu.
2. Makes a throwaway local character and world, so it never touches real saves.
3. Waits for the world and checks the player.
4. Turns on player safety.
5. Runs one of the mod's console commands.
6. Takes a screenshot.

To make it yours, change these two variables to one of your mod's commands and the text it prints:

```yaml
  mod_command: help      # replace with your mod's own console command
  mod_answer: help       # and text its output must contain
```

You can also override them from the command line with `--var mod_command=...`. To run the plan against a game you deployed yourself:

```sh
valheim-cli --expect-strict pins.txt --test tools/dev-loop/smoke-plan.yaml --launch
```

The plan is strict by itself. `game.expect` names a pins file next to the plan, and `game.expectStrict` is true. As a result, `valheim-cli --test` checks every loaded plugin before the first step, even without `--expect-strict`. The dev loop always passes `--expect-strict` with the fresh build's pin, which overrides the plan's file.

## A game on another machine

ValheimCLI listens only on its own machine's loopback. To reach it, open an SSH port forward bound to loopback on both ends. Then pass `--remote`. With it, `valheim-cli` reads no local process or log, and never launches or stops anything:

```sh
ssh -N -L 127.0.0.1:5556:127.0.0.1:5555 user@game-host      # leave running; Ctrl-C closes it
valheim-cli --port 5556 --remote --status
valheim-cli --port 5556 --expect-strict pins.txt
```

In a test, use a game host's `OpenCliTunnelAsync` instead. It opens the same loopback-only forward and refuses one that could listen beyond loopback. See [Valheim.Testing.GameSessions](../../docs/packages/Valheim.Testing.GameSessions.md).

## Limits

- The dev loop deploys to a local game folder, so it works with a local game only.
- A launch without a plan, or `valheim-cli --status`, only brings a game up. Neither is a test result.
- A plan can pass while the mod logs errors. Use `--fail-on` to fail such a round.
- The log summary counts only prefixed log lines. It does not count stack-trace lines.
- BepInEx rewrites the log at every game start, so the summary covers one run.
