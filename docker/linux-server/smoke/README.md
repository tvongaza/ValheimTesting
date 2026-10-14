# Linux dedicated-server smoke

LinuxServerSmoke boots one dedicated server with BepInEx twice: once on a new world and once on its save. It is the boot check for the [Linux image](../README.md) (`native-linux.yml`, `native-server-checks.sh smoke`). It is not an example for mod authors. To test your own mod, see [Check that a mod loads](../../../docs/guides/check-a-mod-loads.md).

## Run it

```sh
# Inside the Linux image, from a writable copy of this repository:
dotnet run --project docker/linux-server/smoke -c Release -- /opt/valheim/server ~/out
```

It prints `PASS` or `FAIL`, the number of passed steps and the output directory.

Usage: `LinuxServerSmoke <disposable-server-runtime> <new-output-directory> [seconds 30..1800] [port]`

| Argument | Rule |
|---|---|
| runtime | A disposable copy without `BepInEx/LogOutput.log` |
| output directory | Must not exist yet |
| seconds | Deadline for all steps, 30..1800, default 600 |
| port | 1024..65533, default 2456; the server also uses the next two |

| Exit code | Meaning |
|---|---|
| 0 | Every step passed within the deadline |
| 1 | A step failed |
| 2 | Invalid usage |

The runtime picks the platform. A Windows server copy also works. On a Mac, use the macOS server (`valheim_server/Valheim`) with native BepInEx; see [Platforms](../../../docs/reference/platforms.md#macos).

## What it checks

The server runs private (`-public 0`) on a generated world, with a throwaway password the tool never prints.

| Step | Passes when |
|---|---|
| 1 | The platform is detected and the launch builds |
| 2 | The owned process starts |
| 3 | `BepInEx/LogOutput.log` shows `Chainloader startup complete` |
| 4 | `server.log` shows `Get create world <generated name>`, then `Opened Steam server` |
| 5 | The server quits when asked; after 60 s it is killed and the step fails |
| 6 | `server.log` shows `World save (5/5) done` |
| 7 to 10 | A second boot starts, loads BepInEx, logs `ZNet.LoadWorld: <name> (<name>), save number` then `Opened Steam server` in `server-2.log`, and stops cleanly |

If you press Ctrl+C or the container stops, the tool still stops the server.

## What it writes

| File | Holds |
|---|---|
| `result.json`, `junit.xml` | Every step and its outcome |
| `server.log`, `server-2.log` | The server log of each boot |
| `savedir` | The world save |
| `boot-1.*`, `boot-2.*` | Each boot's stdout, stderr and BepInEx log copy |

`result.json` also records the platform, Steam build ID, PIDs, each stop, BepInEx error and warning line counts, and `steamBackendConnected`.

## Limits

- It loads no ValheimCLI or mod plugins and uses no pins.
- It does not test networking or gameplay.
- Steam's backend connection (`Game server connected`) is recorded, not required.
- A pass does not mean a clean log. Read the warnings and errors.
- BepInEx writes into the runtime directory, so use a disposable copy or a fresh container.
- The logs can contain the host's network details. Review them before you publish them.
- `scripts/validate.cs` builds the tool but never runs it.
