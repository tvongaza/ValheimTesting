# Linux dedicated-server smoke

`LinuxServerSmoke <disposable-server-runtime> <new-output-directory> [seconds 30..1800] [port]`

Boots one owned dedicated server with BepInEx through `ServerLaunch` and `DirectServerProcess`, waits for startup evidence and stops exactly that process, then boots it again on the saved world. It is written for the [Linux image](../../docker/linux-server/README.md) but takes whatever platform the runtime's executable names, so it also works on a Windows server copy, and on a Mac with the macOS server (`valheim_server/Valheim`; its modded launch needs the native BepInEx described in [getting started](../../docs/getting-started.md)). A Windows or Linux runtime on a Mac stops at the first step.

```sh
# Inside the Linux image, from a writable copy of this repository:
dotnet run --project examples/LinuxServerSmoke -c Release -- /opt/valheim/server ~/out
```

It passes a generated world name, a throwaway password it never prints, `-public 0`, `-savedir <output>/savedir` and `-logFile <output>/server.log`. Steps, each recorded in `result.json` and `junit.xml`:

1. The runtime's platform is detected and the launch built; a runtime that already has `BepInEx/LogOutput.log` is refused so an earlier run's log cannot pass.
2. The owned process starts.
3. BepInEx's `LogOutput.log` reports `Chainloader startup complete`.
4. The server log shows `Get create world <generated name>` and then `Opened Steam server`, which Valheim 1.0 logs once the new world's locations are generated.
5. The process is stopped cleanly: asked to quit (SIGINT; Ctrl+Break over Windows SSH, Ctrl+C on other Windows hosts); if it has not quit within 60 s it is killed and the step fails. `result.json` records each stop (`stop1`, `stop2`).
6. The clean stop saved the world: `World save (5/5) done` in the server log.
7. to 10. The same for a second boot on the same world and save folder (`-logFile <output>/server-2.log`, `boot-2.*` logs): the process starts, BepInEx's chainloader finishes, the server log shows `ZNet.LoadWorld: <name> (<name>), save number` (the saved world, not a new one) and then `Opened Steam server`, and the process stops cleanly.

Exit 0 means every step passed within the deadline (default 600 s); exit 1 is a failure, exit 2 invalid usage. The output directory also holds process stdout/stderr and a copy of the BepInEx log. `result.json` records the platform, Steam build ID, PID, BepInEx error/warning line counts and whether Steam's backend answered (`Game server connected`); that last one is informational, not a pass condition. Read the warnings and errors; a passing smoke does not certify a clean log.

This establishes that the runtime starts, loads BepInEx, generates a new world, saves it at a clean stop and loads that save on the next boot, on that host. It loads no ValheimCLI or mod plugins, uses no pins and does not test networking or gameplay. `scripts/validate.cs` builds it but never runs it.

BepInEx writes its log and configuration into the runtime directory, so use a disposable copy or a fresh container. The logs can contain the host's network details; review them before publishing.
