# Launching servers and clients

What the toolkit finds on this machine with no inventory file, and how `GameLaunch` builds a dedicated-server or client launch on each platform.

## This machine, with no file

With no inventory file (`EnvironmentInventory.Read(null)`, a campaign that leaves out `inventory`, or `valheim-test env list` and `env preflight` without `--inventory`), the inventory is this machine: host `local`, with `local-server` from Valheim Dedicated Server (Steam app 896660) and `local-client` from Valheim (Steam app 892970).

Steam's root is only ever the native default:

| OS | Steam root |
|---|---|
| Windows | the registered `HKCU\Software\Valve\Steam` `SteamPath` |
| macOS | `~/Library/Application Support/Steam` |
| Linux | `~/.steam/steam` |

Each app is then found in every library `steamapps/libraryfolders.vdf` lists, through its `appmanifest_<id>.acf` `installdir`. An install counts only if its executable is there. If Steam is anywhere else (a Flatpak, another folder), list the environment in an [inventory file](../packages/Valheim.Testing.GameSessions.md) with its `install`.

ValheimTesting's own folder is `%LOCALAPPDATA%\ValheimTesting` on Windows, `~/Library/Application Support/ValheimTesting` on macOS and `$XDG_DATA_HOME/ValheimTesting` on Linux. Under it:

| Subfolder | Holds |
|---|---|
| `runs\<environment>` | runtimes |
| `lock` | the host lock |
| `leases` | Steam-account leases |
| `journal` | the run journal |

A no-game test moves the whole folder to a temporary one with a [`FakeDataRoot`](../packages/Valheim.Testing.Game.md#test-fakes) scope.

The ports are ValheimCLI 5688 for the server, 5689 for the client, and game port 2486. These stay away from Valheim's standard 2456, so a personal server on the same machine keeps its port and a crossplay lobby (keyed by public IP and port) never takes another server's joins. Each further local environment takes the next free CLI port and a game port whose three-port range is clear.

### env list and env preflight

```sh
valheim-test env list [--inventory FILE] [--json]
valheim-test env preflight [--inventory FILE] [--json]
```

`env list` prints each detected or assumed value on a `detected:` line, each thing it looked for and did not find on a `NOT FOUND:` line (with every path it tried), and each environment with its host, roles, install, runtime and ports. For a client it also prints the selected architecture, the available slices of a local install and its selected loader. It gives no verdict; it refuses only an inventory it cannot read, for example no Valheim install found.

`env preflight` prints the same lines and says whether a one-off can run. It refuses:

- an inventory without a server and a client;
- a local Mac client whose selected slice cannot launch;
- a run of another process still going, possibly still going, or left `RECOVERABLE` or `UNRECOVERABLE` and not recovered, or a journal it cannot read (`EnvironmentRuns.LocalJournalProblemsAsync`).

A missing dedicated server is reported with three ways forward: install it from Steam (it is free), add a server environment, or run `valheim-test start` for a hosted local world.

### A file that names some environments

A file lists the environments to use, in preference order, and never gains this machine's environments unless it lists them. An environment on this machine needs only its name and role; install, runtime and ports are filled in as above:

```json
{ "name": "local-client", "roles": ["client"] }
```

If the file names its own `local`-kind host, environments without a host go to it. `leaseHost` and `leaseDirectory` default to this machine's only when every client is on it. Leave `lock` and the leases out to share the defaults with other inventories on this machine. Every run's `result.json` records what was detected (`inventoryDetected`); a run with no file records `inventory: this machine`.

## Dedicated server launch

`GameLaunch.ForServer` builds an owned dedicated-server launch from a copied runtime directory, and `ToStartInfo()` renders it as the `ProcessStartInfo` that `DirectServerProcess` starts. The platform comes from the runtime's contents (`GameLaunch.DetectServer`); a runtime with both or neither executable is refused.

| Platform | Detected from | Started as |
|---|---|---|
| Windows | `valheim_server.exe` | the executable, directly |
| Linux | `valheim_server.x86_64` | the executable, with `start_server_bepinex.sh`'s environment |
| macOS | `valheim_server/Valheim` | through `/usr/bin/arch` ([below](#macos-dedicated-server)) |

Before building, it requires BepInEx's preloader and core and the platform's Doorstop loader (on Windows `winhttp.dll` and a `doorstop_config.ini` that enables Doorstop and targets the preloader), so a broken install is refused instead of starting a vanilla server. A caller value for `DOORSTOP_ENABLED`, `DOORSTOP_TARGET_ASSEMBLY` or `DOORSTOP_DISABLE`, or a `--doorstop-*` argument, is refused on every platform; the same variables are removed if inherited. Plugin pins after startup remain the proof that BepInEx ran ([Actors and pins](game-actors-and-pins.md)).

On Linux it also requires the executable's user-execute bit and reproduces what BepInExPack_Valheim's `start_server_bepinex.sh` exports: `DOORSTOP_ENABLED`, `DOORSTOP_TARGET_ASSEMBLY`, `linux64` and `doorstop_libs` prepended to `LD_LIBRARY_PATH`, and `libdoorstop_x64.so` prepended to `LD_PRELOAD`. Existing entries are kept; no empty entry is added. `SteamAppId` defaults to 892970 on every platform. Arguments stay the caller's plan.

A machine may build the other platform's server launch for inspection (a Windows machine builds a Linux launch it cannot run); callers that launch check the machine themselves. Given a host platform (`hostPlatform: ServerPlatform.Windows` or `Linux`), the same builder makes the launch `HostServer.StartAsync` starts on a game host: the runtime is an absolute path there, only the path and variable-name rules are checked here, and the host checks the launch's `RequiredFiles` before it starts anything.

A Linux dedicated server can run in the [local container image](../../docker/linux-server/README.md).

## macOS dedicated server

The macOS dedicated server is `valheim_server/Valheim` with its `valheim_server/Data` folder, from Steam app 896660's macOS build ([platforms](platforms.md#macos) has the install). `GameLaunch.DetectServer` returns `MacOS` for it. It runs only on a Mac, and a Mac refuses the Windows and Linux servers before reading their mode, naming the macOS server, the Linux container (`--platform linux/amd64`, experimental on Apple Silicon) or a remote Windows or Linux host.

On a Mac, `ToStartInfo()` starts the server through `/usr/bin/arch -arm64` (or `-x86_64` on an Intel Mac) with `-e DYLD_INSERT_LIBRARIES=<the runtime's libdoorstop.dylib>` and `-e DYLD_LIBRARY_PATH=<runtime>/valheim_server`: the kernel strips `DYLD_*` from that SIP-protected launcher's environment, and a universal server otherwise starts as its parent's architecture. Doorstop is enabled for BepInEx's preloader. The server and the Doorstop library must both have the machine's slice. `arch` executes the server in place, so the PID is the server's own and a clean stop is a SIGINT, as on Linux ([stopping owned processes](game-waiting.md#stopping-owned-processes)).

`GameLaunch.DetectServer` refuses a macOS game client: a `Valheim.app` bundle, a renamed bundle recognised by `Contents/MacOS/Valheim`, or a directory holding `Valheim.app`.

## Game client launch

`GameLaunch.ForClient` builds the launch of one BepInEx game client from its install directory and starts nothing; `ToStartInfo()` renders it for this machine. `ClientSession.Launch` builds its owned launch this way ([client sessions](game-client-session.md)). The platform comes from the install's contents (`GameLaunch.DetectClient`), never the host, and `DetectClient` works on any host.

| Refused install | Fix |
|---|---|
| More than one client executable | one install per directory |
| The `Valheim.app` bundle itself | pass the directory that holds it and BepInEx |
| A dedicated-server runtime | use `GameLaunch.ForServer` (`DetectServer` likewise names `ForClient` for a client) |
| A Linux host, or `valheim.x86_64` | "Linux hosts run the dedicated server only; start the client on Windows or macOS." |
| Any host other than the install's OS | `PlatformNotSupportedException`; no Wine or Proton |

| | Windows | macOS |
|---|---|---|
| Detected from | `valheim.exe` | `Valheim.app/Contents/MacOS/Valheim` (user-execute bit required) |
| Loader files required | `BepInEx/core/BepInEx.Preloader.dll` and `BepInEx.dll`; `winhttp.dll`; `doorstop_config.ini` enabling Doorstop and targeting `BepInEx\core\BepInEx.Preloader.dll` (any case, either separator; another existing DLL is refused) | BepInEx core; `doorstop_libs/libdoorstop_x64.dylib` (BepInExPack_Valheim) or a root `libdoorstop.dylib` with the requested slice; for arm64 also `BepInEx/core/MonoMod.RuntimeDetour.dll` 25 or later |
| Started executable | `valheim.exe` | `/usr/bin/arch -x86_64` or `-arm64`, then the bundle executable |
| Loader variables set | none; the proxy DLL reads its ini | `DOORSTOP_ENABLED=1`, `DOORSTOP_TARGET_ASSEMBLY`, the library's full path prepended to `DYLD_INSERT_LIBRARIES`, passed with `arch -e` |
| Architectures | x64 | arm64 by default on Apple Silicon, x86_64 on Intel; `architecture` selects another slice |
| Also needed to run | desktop session, Steam | logged-in desktop session, Steam |

On every platform `-console` is added first unless the caller passes `console: false` or already includes it; other arguments follow unchanged. `SteamAppId` defaults to 892970. Caller environment is applied first, with existing search-list entries kept. The same Doorstop overrides as for a server (`DOORSTOP_ENABLED`, `DOORSTOP_TARGET_ASSEMBLY`, `DOORSTOP_DISABLE`, `--doorstop-*`) are refused because they would start a vanilla client. A macOS install path may not contain `:`, which `DYLD_INSERT_LIBRARIES` cannot represent. The loader check that pairs a Doorstop proxy with its configuration is on the [preflight page](game-preflight.md#what-the-preflight-catches).

### The macOS slice and core check

`ToStartInfo()` reads the Mach-O slices of the game and of each Doorstop library and inserts the first library with the requested slice; otherwise it refuses, naming the slices it found. `arch` fails rather than start another slice, so an arm64 request never falls back to Rosetta. BepInExPack_Valheim ships only an x86_64 library and a core on legacy MonoMod: the Rosetta route. The native arm64 route needs a `libdoorstop.dylib` with an arm64 slice and a BepInEx core whose `BepInEx/core/MonoMod.RuntimeDetour.dll` is version 25 or later (read, never loaded); otherwise the arm64 launch is refused with the version it found. `GameLaunch.ClientLaunchArchitectures` reports the slices an install can be launched as. The install recipe and limits are in [Native Apple Silicon client](platforms.md#native-apple-silicon-client).

## Limits

- `GameLaunch.ForClient` only builds the launch. A game client needs an interactive desktop session with a display, a GPU and a running, signed-in Steam client; a process started from a service, a scheduled task without a logged-in user or an SSH session has no display. Starting a client inside a user's session on another machine is [`InteractiveClient`'s job](../packages/Valheim.Testing.GameSessions.md).
- There is no Wine or Proton support, and Linux hosts run the dedicated server only.
- Nothing on disk says whether each plugin's own hooks and native libraries work on arm64.
- Server container images contain game files and must never be pushed or published.
