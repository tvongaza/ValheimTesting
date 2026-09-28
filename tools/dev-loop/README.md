# Developer-loop scripts

Scripts for a mod's edit-build-test loop against a local game. They drive the `valheim-cli` executable; they are not part of the libraries and never run during `scripts/validate.cs` except under the inert-tool tests described below.

| Script | What it does |
|---|---|
| `dev-loop.sh` / `dev-loop.ps1` | Build a mod, install its DLL into `BepInEx/plugins`, launch Valheim and run a test plan in strict mode, then summarise the log |
| `pin-mods.sh` / `pin-mods.ps1` | Snapshot the plugin pins of a running game, check a game against them (strict), or run a command only on a matching game |
| `log-summary.sh` | Count the warnings and errors in `BepInEx/LogOutput.log` by level, source and message; `dev-loop.sh` runs it after every round |

These moved here from ValheimCLI (commit `ee4cd23`) on 28 September 2026: ValheimCLI stays a minimal executable and game-side API, and the support scripts live with the testing toolkit. Their behaviour did not change.

## Get valheim-cli

ValheimTesting does not build the executable. Take `valheim-cli` from a [ValheimCLI](https://github.com/tvongaza/valheimCLI) release, or build it from source:

```sh
git clone https://github.com/tvongaza/valheimCLI.git
cd valheimCLI/CLI
dotnet build -c Release     # CLI/bin/Release/net9.0/valheim-cli (valheim-cli.exe on Windows)
```

Use a build at or after the ValheimCLI commit pinned in [`cli-dependency.json`](../../cli-dependency.json): the scripts need `--status` with its `local_process=` field, `--expect-strict`, `--test`, `--launch`, `--progress`, `--stall` and `manifest --write`. The game needs the matching `valheimCLI.dll` in `BepInEx/plugins`. Put `valheim-cli` on `PATH`, or point `VALHEIM_CLI` at it.

## bash or PowerShell

The dev-loop and pin-mods scripts come as twins with the same steps, environment variables and exit codes: bash for macOS and Linux, PowerShell for Windows (Windows PowerShell 5.1 or PowerShell 7). Keep the two in step when changing either. Run them from your mod's checkout:

| | macOS / Linux (bash) | Windows (PowerShell) |
| --- | --- | --- |
| build, deploy, run a plan | `tools/dev-loop/dev-loop.sh MyMod.csproj plan.yaml` | `powershell -ExecutionPolicy Bypass -File tools\dev-loop\dev-loop.ps1 MyMod.csproj plan.yaml` |
| snapshot pins | `tools/dev-loop/pin-mods.sh snapshot pins.txt` | `powershell -ExecutionPolicy Bypass -File tools\dev-loop\pin-mods.ps1 snapshot pins.txt` |
| check pins (strict) | `tools/dev-loop/pin-mods.sh check pins.txt` | `powershell -ExecutionPolicy Bypass -File tools\dev-loop\pin-mods.ps1 check pins.txt` |
| summarise the last run's log | `tools/dev-loop/log-summary.sh` | printed by `dev-loop.ps1` |

Prefix the paths with wherever your ValheimTesting checkout is. Set the environment in each shell's own way:

```bash
VALHEIM_CLI=/path/to/valheim-cli VALHEIM_EXPECTATIONS=pins.txt tools/dev-loop/dev-loop.sh MyMod.csproj smoke-plan.yaml
```

```powershell
$env:VALHEIM_CLI = 'C:\path\to\valheim-cli.exe'
$env:VALHEIM_EXPECTATIONS = 'pins.txt'
powershell -ExecutionPolicy Bypass -File tools\dev-loop\dev-loop.ps1 MyMod.csproj smoke-plan.yaml
```

ValheimCLI's `examples/smoke-plan.yaml` is a plan to start from. `VALHEIM_PATH` defaults to the Steam folder of each system; on Windows that is `C:\Program Files (x86)\Steam\steamapps\common\Valheim`. Where `dev-loop.sh` ends with `log-summary.sh`, `dev-loop.ps1` prints the log's warning, error and fatal counts by level and source. Each script's header lists its environment variables and exit codes.

The game must not be running when `dev-loop` deploys: a running game keeps the old build (and on Windows locks the file). The script refuses to deploy unless `valheim-cli --status` reports `local_process=false`.

## Strict pins in the dev loop

`dev-loop <MyMod.csproj> <plan.yaml>` runs the plan in strict mode against `VALHEIM_EXPECTATIONS`. Every rebuild changes the mod's md5, so the script never passes that file as it is, and never accepts whatever md5 the game reports: it hashes the DLL it just built and deploys, writes a temporary copy of the pins file in which only that mod's pin is replaced, checks the installed copy matches, and passes the copy to `--expect-strict`. The pins file must pin the mod exactly once, under its DLL name or under the key named by `VALHEIM_PLUGIN_KEY` (its GUID or name); otherwise the script stops before deploying. Every other pin, including the core and pack hashes, is kept as written. See ValheimCLI's `docs/expectations.md` for the pins file format.

## Tests

`tests/Valheim.Testing.Tests` runs the real scripts against inert fake tools in a temporary directory; nothing is built, installed or launched. `DevLoopGuardTests` and `StrictExampleTests` run the bash scripts on macOS and Linux and are reported as skipped on Windows. `DevLoopGuardPowerShellTests` and `StrictExamplePowerShellTests` run the same cases against the PowerShell twins under Windows PowerShell on Windows and are reported as skipped elsewhere. `dotnet run scripts/validate.cs` runs them with the rest of the library tests.
