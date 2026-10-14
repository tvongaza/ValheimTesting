# Launching servers and clients

How `GameLaunch` builds the launch of a BepInEx dedicated server or game client on each platform, and what it refuses.

`GameLaunch` is in [`Valheim.Testing.Game`](../packages/Valheim.Testing.Game.md). It builds a launch as data and starts nothing; `ToStartInfo()` renders it as a `ProcessStartInfo` for this machine. Runners call it for you. Call it yourself only when you start a game process outside a session, for example for an [owned server session](game-server-session.md). Which installs a run uses comes from [this machine's inventory](game-inventory.md).

## Dedicated server launch

`GameLaunch.ForServer(runtime, arguments, environment, hostPlatform)` builds an owned dedicated-server launch. `ToStartInfo()` renders it for `DirectServerProcess` ([owned server session](game-server-session.md)). `GameLaunch.DetectServer` reads the platform from the runtime's files, never from the host:

| Platform | Detected from | Started as |
|---|---|---|
| Windows | `valheim_server.exe` | the executable, in a console of its own with no window |
| Linux | `valheim_server.x86_64` | the executable, with `start_server_bepinex.sh`'s environment |
| macOS | `valheim_server/Valheim` and `valheim_server/Data` | through `/usr/bin/arch` ([below](#macos-dedicated-server)) |

The Windows server's own console lets a clean stop signal the server, not the runner. Your arguments stay as you pass them; a pinned plan's rules are on [pinned server runner](sessions-pinned-server-run.md#the-plan).

`ForServer` refuses, before anything starts:

| Refused | Why |
|---|---|
| A runtime with more than one server executable, or none | It never guesses the platform |
| A macOS game client (`Valheim.app`, or a folder holding it) | Use `GameLaunch.ForClient` |
| No `BepInEx/core/BepInEx.Preloader.dll` or `BepInEx/core/BepInEx.dll`, or a nested `BepInEx/core/core` | The server would start without BepInEx |
| Windows: no `winhttp.dll`, a `doorstop_config.ini` that does not enable Doorstop and target the preloader, or a `winhttp.dll` and `doorstop_config.ini` from different Doorstop versions | Same |
| Linux and macOS: no user-execute bit on the server | It cannot start |
| Linux: no `doorstop_libs/libdoorstop_x64.so`, or `:` or `;` in the path | The server would start without BepInEx; the search lists cannot hold those characters |
| `DOORSTOP_ENABLED`, `DOORSTOP_TARGET_ASSEMBLY` or `DOORSTOP_DISABLE` in your environment, or a `--doorstop-*` argument | They would disable or redirect the loader |

The same Doorstop variables are removed if this process inherited them. On Linux the launch reproduces BepInExPack_Valheim's start script: `DOORSTOP_ENABLED`, `DOORSTOP_TARGET_ASSEMBLY`, `linux64` and `doorstop_libs` in front of `LD_LIBRARY_PATH`, and `libdoorstop_x64.so` in front of `LD_PRELOAD`. Existing entries stay behind them. `SteamAppId` defaults to 892970 on every platform.

When a runner starts a Linux or macOS server from a disposable profile, the process starts in the profile's loader folder, and inherited `LD_*` or `DYLD_*` search lists are cleared first. A Windows profile's launch folder holds both the executable and the Doorstop proxy.

A Windows machine may build a Linux launch to inspect it. With `hostPlatform` (`ServerPlatform.Windows`, `Linux` or `MacOS`) the launch is for a game host: `runtime` is an absolute path there, only path and variable-name rules are checked here, and `HostServer.StartAsync` checks the launch's `RequiredFiles` on the host. `ToStartInfo()` refuses a host launch. A Linux server can also run in the [local container image](../../docker/linux-server/README.md).

## macOS dedicated server

The macOS dedicated server is `valheim_server/Valheim` with its `valheim_server/Data` folder, from Steam app 896660's macOS build ([platforms](platforms.md#macos)). It runs only on a Mac. A Mac refuses the Windows and Linux servers and names the alternatives: the macOS server, the Linux container (`--platform linux/amd64`) or a remote Windows or Linux host.

`ToStartInfo()` starts it through `/usr/bin/arch -arm64` on Apple Silicon, or `-x86_64` on an Intel Mac, so a universal server runs as the machine's own slice. It passes `-e DYLD_INSERT_LIBRARIES=` with the runtime's Doorstop library and `-e DYLD_LIBRARY_PATH=<runtime>/valheim_server`, because the kernel strips `DYLD_*` from that protected launcher's environment. The server and the Doorstop library must both have the machine's slice. `arch` runs the server in place, so the PID is the server's own and a clean stop is a SIGINT.

## Game client launch

`GameLaunch.ForClient(install, arguments, environment, hostPlatform, architecture, console, secretVariables)` builds the launch of one BepInEx game client and starts nothing. [`ClientSession.Launch`](game-client-session.md) builds its owned launch the same way. `GameLaunch.DetectClient` reads the platform from the install's contents and works on any host.

| | Windows | macOS |
|---|---|---|
| Detected from | `valheim.exe` | `Valheim.app/Contents/MacOS/Valheim`, with the user-execute bit |
| Loader files required | BepInEx core; `winhttp.dll`; `doorstop_config.ini` that enables Doorstop and targets the preloader | BepInEx core; `doorstop_libs/libdoorstop_x64.dylib` or a root `libdoorstop.dylib` with the requested slice |
| Started as | `valheim.exe` | `/usr/bin/arch -arm64` or `-x86_64`, then the bundle executable |
| Loader variables | none; the proxy reads its ini | `DOORSTOP_ENABLED=1`, `DOORSTOP_TARGET_ASSEMBLY`, `DYLD_INSERT_LIBRARIES` passed with `arch -e` |
| Architectures | x64 only | arm64 by default on Apple Silicon, x86_64 on an Intel Mac |

`-console` goes first unless you pass `console: false` or include it. Your arguments follow unchanged. `SteamAppId` defaults to 892970. The Doorstop variables and `--doorstop-*` arguments are refused as for a server, and a macOS install path may not contain `:`.

An owned launch adds `-logFile` last, naming `toolkit-unity.log` in the run's launch folder. Unity then never writes the user's own `Player.log`, and nothing is left in the install. An owned launch refuses your own `-logFile` argument. A launch you build with `ForClient` keeps Unity's default log.

| Refused install | Fix |
|---|---|
| More than one client executable | One install per directory |
| The `Valheim.app` bundle itself | Pass the directory that holds it and BepInEx |
| A dedicated-server runtime | Use `GameLaunch.ForServer` |
| `valheim.x86_64`, or a Linux machine | "Linux hosts run the dedicated server only; start the client on Windows or macOS." |
| An install for another OS than this machine | `PlatformNotSupportedException`; no Wine or Proton |
| `arm64` for a Windows client | The Windows client is x64 only |

A Windows install whose Doorstop proxy and configuration come from different Doorstop versions: `ForServer` and `ForClient` refuse it (an `InvalidOperationException`); the [preflight](game-preflight.md#what-the-preflight-catches) reports it first.

### The macOS slice and core check

`ToStartInfo()` reads the Mach-O slices of the game and of each Doorstop library, and inserts the first library with the requested slice. Without one, it refuses and names the slices it found. `arch` fails rather than start another slice, so an arm64 request never falls back to Rosetta.

| Route | Needs |
|---|---|
| arm64, native (the default on Apple Silicon) | a `libdoorstop.dylib` with an arm64 slice, and `BepInEx/core/MonoMod.RuntimeDetour.dll` version 25 or later |
| x86_64, under Rosetta | BepInExPack_Valheim's `doorstop_libs/libdoorstop_x64.dylib`; select `x64` explicitly |

The MonoMod version is read, never loaded; an older core is refused with the version found. `GameLaunch.ClientLaunchArchitectures(install)` returns the slices an install can launch as. The native install recipe is in [Native Apple Silicon client](platforms.md#native-apple-silicon-client).

## Limits

- `GameLaunch` only builds launches. A game client needs an interactive desktop session with a display, a GPU and a running, signed-in Steam client. A service, an SSH session or a scheduled task without a logged-in user has no display. Starting a client in a user's session on another machine is [`InteractiveClient`'s job](sessions-hosts.md#clients-on-a-host).
- There is no Wine or Proton support. Linux hosts run the dedicated server only.
- No file says whether each plugin's own hooks and native libraries work on arm64.
- Server container images contain game files. Never push or publish them.
