# ScriptEngine extension reload check

This is a maintainer's check of ValheimCLI's ScriptEngine extension lifecycle (`ExtensionReload`). It is not an example for mod authors. It attaches to a running game at the main menu through the ValheimCLI transport. It installs revision A of a probe extension, replaces it with revision B, then removes it. It does not launch or stop Valheim, create a world, or select a character.

## Before you run it

- Keep the ValheimCLI core in `BepInEx/plugins`.
- Enable ScriptEngine's `LoadOnStart` and `EnableFileSystemWatcher`.
- Use an empty, dedicated `BepInEx/scripts` directory. ScriptEngine reloads every script when any DLL changes.
- Run it only against a session you own.

## Use

The probe in [`ReloadProbe/`](ReloadProbe/Plugin.cs) is a ScriptEngine script that registers the `example.probe` extension. It builds against your game and the exact core the game runs, through [`tools/game-references`](../game-references/README.md). Pass `-p:ValheimPath=` (or set `VALHEIM_PATH`) and `-p:CliDll=`. It never builds in this repository's CI.

From the repository root, build both probe revisions and the driver, then run the driver:

```sh
# From this repository; CliDll is the valheimCLI.dll installed in the game's BepInEx/plugins.
dotnet build tools/reload-check/ReloadProbe/ReloadProbe.csproj -c Release -p:ValheimPath=/path/to/Valheim \
  -p:CliDll=/path/to/Valheim/BepInEx/plugins/valheimCLI.dll -o artifacts/probe-a
dotnet build tools/reload-check/ReloadProbe/ReloadProbe.csproj -c Release -p:ValheimPath=/path/to/Valheim \
  -p:CliDll=/path/to/Valheim/BepInEx/plugins/valheimCLI.dll -p:ProbeRevision=B -o artifacts/probe-b
dotnet build tools/reload-check/ReloadCheck.csproj -c Release

# Use absolute paths to the two built probe DLLs:
dotnet tools/reload-check/bin/Release/net10.0/ReloadCheck.dll \
  5555 /absolute/artifacts/probe-a/ReloadProbe.dll /absolute/artifacts/probe-b/ReloadProbe.dll \
  /absolute/disposable-game/BepInEx/scripts /absolute/results/reload.json /private/menu-pins.txt
```

On success the driver prints this line:

```text
PASS: A -> B reload, active cancellation, resource cleanup, removed command, stable core and connection, final unregister.
```

The probe outputs contain the probe only, with embedded symbols, because ScriptEngine expects them. Do not copy a second ValheimCLI assembly into `scripts`.

## Arguments

| Position | Argument | Meaning |
|---|---|---|
| 1 | `<port>` | The actual ValheimCLI port on `127.0.0.1` (replace `5555`) |
| 2 | `<probe-A.dll>` | Absolute path to revision A |
| 3 | `<probe-B.dll>` | Absolute path to revision B (`-p:ProbeRevision=B`) |
| 4 | `<empty-scripts-dir>` | The dedicated `BepInEx/scripts`; refused if it holds a DLL |
| 5 | `<report.json>` | Where the evidence report is written |
| 6 | `<pins-file>` | Strict pins: core, ScriptEngine, every other loaded plugin, world UID |

The check is local. The DLL replacement must reach the same game as the loopback connection. Include the world UID in the pins file only if you run in a world.

The driver pins the probe absent first, then to each DLL's MD5, then absent again. Every command gets a `cli_expect --strict` preflight. If the game's `[Expectations]` file is enabled, keep it consistent; the driver does not change it.

## What the check requires

| Requirement | Proved by |
|---|---|
| B is a new instance and answers on the original connection | `example.probe/hello` returns revision `0.2.0` and a new instance |
| A's active request ends with A's instance token | `example.probe/wait` on a second connection returns `extension_unloaded` |
| A's removed command cannot run old code | `example.probe/removed` returns `no_extension_command` |
| A's resource was destroyed | `hello` reports exactly one lease |
| Core build identity and load time are unchanged | `cli_build` output matches the first reading |
| Deleting the file unregisters the probe | `WaitForRemoval` passes, then `cli_build` still answers |

The replacement and the waits are `ExtensionReload.Install`, `WaitForReplacement` and `WaitForRemoval`. Other hot-reload smoke tests can use them.

## Results

| Exit code | Meaning |
|---|---|
| `0` | Every requirement held |
| `1` | A step failed; the message goes to standard error |
| `2` | Wrong number of arguments; prints the usage line |

The report holds `passed` and an `evidence` list of commands, replies and checks. It can contain local paths from `cli_build`, so review it before publishing. The probe logs iterator disposal and registered cleanup separately. The driver deletes only the probe it installed. You shut down the game and check restoration.

## Limits

The check does not cover world mutations, delayed quiescence after a mutation, queued mutation cancellation, client rendering, or actual assembly memory reclamation.
