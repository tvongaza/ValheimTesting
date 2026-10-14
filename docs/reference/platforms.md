# Platforms

This page shows which host OS runs which role, the one shell each OS uses, and the extra setup a Mac needs.

The usual setup is one Windows PC with Valheim installed through Steam. That setup needs nothing on this page ([on one Windows PC](../packages/Valheim.Testing.Game.md#on-one-windows-pc)). Every host needs the .NET 10 SDK. A Windows host also needs PowerShell 7 ([host shells](#host-shells)).

## Support matrix

| Host | Toolkit and tests | Game client | Dedicated server |
|---|---|---|---|
| Windows | Yes | `valheim.exe` | `valheim_server.exe` |
| Linux | Yes | No | `valheim_server.x86_64` |
| macOS | Yes | `Valheim.app`, native arm64 or x86_64 under Rosetta | `valheim_server/Valheim`, native arm64 |

CI runs bootstrap and validation on all three. [Launching servers and clients](game-launch.md) describes how each role starts.

Other hosts:

- **Server in a container.** You can run the server in a Linux x86-64 image ([docker/linux-server](../../docker/linux-server/README.md)). On Apple Silicon the image runs only under emulation and is experimental.
- **Remote hosts.** A remote host is an SSH game host in an environment inventory ([Valheim.Testing.GameSessions](../packages/Valheim.Testing.GameSessions.md)). A headless dedicated server starts there from a disposable loader profile of the host's install, without Steam or a desktop session.
- **Remote clients.** A client needs an interactive desktop session with a display and a running Steam client. `InteractiveClient` starts one inside a Windows or SSH Mac host's existing desktop session. A remote Mac client needs the SSH user at the unlocked console, with Steam signed in. <!-- TODO(docs): #808 -->

## Host shells

Every script the toolkit runs on a host runs in that OS's one shell:

- On Windows, the shell is `pwsh` (PowerShell 7.2 or later).
- On macOS and Linux, the shell is `bash`.

The toolkit does not use Windows PowerShell 5.1. It does not use PowerShell on macOS or Linux. Each host in an environment inventory names its `shell` to match, and the toolkit refuses any other combination. This page is the one place the requirement is written; other pages link here.

PowerShell 7.2 or later is needed because the scheduled tasks the toolkit registers start the same `pwsh.exe` that registered them.

If a Windows host has no `pwsh` on its PATH, open a desktop terminal on that machine and install it. Over SSH, the installer fails with access denied.

```sh
winget install --id Microsoft.PowerShell --source winget
```

This installs the current PowerShell release. If winget installs the packaged (MSIX) PowerShell, the scheduled tasks start that `pwsh.exe` too.

If you reach the host over SSH, restart its OpenSSH server (`Restart-Service sshd`) or the machine after installing. New sessions then see the new PATH.

`valheim-test env preflight` and a session's host checks refuse a Windows host without `pwsh`. They print the `winget` command above.

## Windows

| Role | Executable | Steam |
|---|---|---|
| Game client | `valheim.exe` | Steam client running and signed in, in the same desktop session |
| Dedicated server | `valheim_server.exe` (Valheim Dedicated Server, app 896660) | Not needed |

Both run from Steam installs.

Another Windows machine can host the server over SSH. A headless server starts there from a disposable loader profile of its install, without Steam or a desktop session.

Run `valheim-test` and the runners from an ordinary terminal: Windows Terminal, PowerShell or cmd. Never run them from inside an MSIX-packaged app, such as a desktop app that starts commands for you. Windows redirects that app's AppData writes into its package, where the server's scheduled task and the game cannot see them. For that reason, `env preflight` and a run's host checks refuse a packaged process.

## Linux and containers

Linux hosts run the dedicated server only. Linux machines detect no client environment. A Linux game client is refused with:

```text
Linux hosts run the dedicated server only; start the client on Windows or macOS.
```

[docker/linux-server](../../docker/linux-server/README.md) builds a local x86-64 image with the server, BepInEx and .NET 10. [LinuxServerSmoke](../../docker/linux-server/smoke/README.md) boots the server in that image twice, on a new world and on its save. The [scheduled server checks](../../docker/linux-server/README.md#scheduled-checks-in-ci) run every night and after each new public server build. They run that smoke and the server half of the [native acceptance suite](../../tests/Valheim.Testing.NativeAcceptance/README.md).

You can place a server on another host from any OS. Both options are described in [Valheim.Testing.GameSessions](../packages/Valheim.Testing.GameSessions.md):

- `PinnedServerRun --inventory` runs a plan's dedicated server on an inventory host. That host can be Linux over SSH, Linux in a container, or Windows.
- A campaign (`PinnedServerRun.RunCampaignAsync`) also places remote clients. Each client starts in its host's desktop session, on the Steam identity signed in there. The identity is leased, so no two runs use it at once.

You provision the host and sign in its Steam client yourself.

Server images contain Valheim's game files. Build them locally, or in the CI job that uses them. Never push, upload or publish an image, a layer or a server directory.

## macOS

The dedicated server runs natively on a Mac. Install it with anonymous SteamCMD:

```sh
steamcmd +force_install_dir <dir> +login anonymous +app_update 896660 validate +quit
```

This writes a server install into `<dir>`. On Apple Silicon, SteamCMD runs under Rosetta, but the server does not.

The install is not an app bundle:

- The server is `valheim_server/Valheim`, a universal (x86_64 and arm64) executable.
- Unity's and Mono's libraries and its `Data` folder sit beside it.
- `steamapps/appmanifest_896660.acf` records the build id.

Before you compare a Mac server with a client on another OS, check that the build ids match. A macOS server runs only on a Mac. A Mac runs neither the Windows nor the Linux server. A `Valheim.app` client is refused as a server runtime. [macOS dedicated server](game-launch.md#macos-dedicated-server) describes the launch.

Modded runs need BepInEx that runs natively on Apple Silicon. BepInExPack_Valheim's Doorstop and core are x86_64 only. Use an arm64 Doorstop and BepInEx core, as for the [native client](#native-apple-silicon-client).

### Steam runtime package

The server depot has no local `steamclient.dylib`. A profile run takes SteamCMD's native libraries from a reviewed manifest instead. Capture the manifest on the Mac:

```sh
valheim-test steam-runtime capture --steamcmd DIR --output steam-runtime.json
```

The command writes `steam-runtime.json`. The manifest records the SteamCMD build, its `root` and a SHA-256 hash for each file. Capture starts from `steamclient.dylib` and `crashhandler.dylib` and follows every `@loader_path` dependency. It refuses a dependency outside `/System/Library/` and `/usr/lib/`.

| Exit | When |
|---|---|
| `0` | The manifest is written. |
| `2` | The arguments are wrong. The command prints its usage. |
| `3` | `REFUSED: …`, for example: the output file exists, the host is not macOS, or a dependency is unpinned. |

Use the manifest in one of two ways:

- In a profile campaign, set the server role's `steamRuntimePackage` to the manifest.
- With `valheim-test server-load`, pass `--steam-runtime-package FILE`.

Preparation checks the following on the host, before staging and again after making the signed copies:

- the SteamCMD build record
- the full `@loader_path` dependency closure
- the arm64 slices
- the source SHA-256 hashes

The run copies only the pinned files into its disposable profile. It thins them to arm64 and signs them locally, because dyld can reject Valve's original signatures. It checks the staged hashes before every boot and records the source manifest in private evidence. It never modifies SteamCMD or the game install, and it bundles no Valve files.

Only the pinned files are hashed, so extra files in the SteamCMD folder never reach the profile. An extra file in the staged profile's `steam-runtime/` folder is refused.

A refused package names the file that changed. The refusal carries the verification script's `VT-STEAM-RUNTIME <reason> [file]` line, for example `VT-STEAM-RUNTIME changed libaudio.dylib`:

| Reason | Meaning |
|---|---|
| `changed` | A pinned file's hash differs. |
| `missing` | A pinned file is gone. |
| `linked` | A pinned file's path has a symbolic link in it. |
| `architecture` | A pinned file has no arm64 slice. |
| `manifest-changed` | `package/steam_cmd_osx.manifest` differs. |
| `version-changed` | The SteamCMD build differs. |
| `manifest` | `package/steam_cmd_osx.manifest` is missing or linked. |
| `source` | The SteamCMD folder is missing or linked. |
| `unsupported` | The host is not macOS. |
| `exists` | The profile already has a `steam-runtime/` folder. |

If you update SteamCMD, re-capture the manifest.

For an SSH Mac server campaign, keep the signed SteamCMD files on the Mac and make the manifest available to the runner. The manifest's `root` still names the Mac path. The campaign checks that remote source and stages the same disposable profile.

## Native Apple Silicon client

The macOS game client is universal: `lipo -info <install>/valheim.app/Contents/MacOS/Valheim` lists `x86_64 arm64`. A modded client can run either way.

| Architecture | Runs as | Install needs | Use it for |
|---|---|---|---|
| `arm64` | native arm64 | arm64 `libdoorstop.dylib` at the install's root; MonoMod 25 core | Mods with no Intel-only native parts |
| `x64` | x86_64 (Rosetta on Apple Silicon) | BepInExPack_Valheim as installed (`doorstop_libs/libdoorstop_x64.dylib`) | Mods with Intel-only native parts |

The Doorstop library comes from UnityDoorstop 4.5 or later. A universal build and an arm64-only build both have the arm64 slice.

The core matters as much as the loader. BepInExPack_Valheim's core uses legacy MonoMod, which cannot apply Harmony hooks on arm64. A native core is BepInEx 5.4.23.5 rebuilt on HarmonyX 2.16.1 and MonoMod 25 ([source branch](https://github.com/bbauti/BepInEx/tree/codex/macos-arm64-valheim)). It is a community build, not an upstream BepInEx release.

`GameLaunch.ForClient` refuses an arm64 plan before anything starts if any of these is true:

- the game or the Doorstop library has no arm64 slice
- `BepInEx/core/MonoMod.RuntimeDetour.dll` is missing or older than 25

`ClientRunPlan.Validate` runs the same check on a local install. A runner's `validate` refuses such a plan, and `run` refuses it before starting the server. The game starts through `/usr/bin/arch -arm64`, so it never falls back to Rosetta. Windows clients are x64 only, so `ClientRunPlan.Validate` and a remote campaign client refuse `arm64` for them.

Defaults:

- `valheim-test start` and `server-load` select arm64 for a local Apple Silicon client and x64 for an Intel Mac.
- A hand-written `ClientRunPlan` and `GameLaunch.ForClient` also default to arm64 on Apple Silicon.
- A remote Mac inventory entry defaults to arm64 when its CPU is unknown. For an Intel Mac, set `"architecture": "x64"`.
- Campaign plans and regression inputs that omit the architecture inherit the inventory's choice.

If you want Rosetta, set `"architecture": "x64"` in the inventory or the plan. You can also pass `ClientArchitecture.X64`, or `--client-architecture x64` for one run.

### Install a native loader

The community installer [Relokk1/valheim-native-arm64](https://github.com/Relokk1/valheim-native-arm64) sets up a native client. Read `install.sh` before you run it. Use a test install and a disposable local character, never your own.

```sh
git clone https://github.com/Relokk1/valheim-native-arm64.git
cd valheim-native-arm64
git checkout cd5565a82dbff8332989c812cb141ad5638dbd52
./install.sh "<client install>"   # the folder that holds valheim.app
```

The script makes these changes:

- It downloads BepInExPack_Valheim 5.4.2333 and replaces `BepInEx/core` with its rebuilt core.
- It copies UnityDoorstop 4.5.0's universal `libdoorstop.dylib` to the install's root.
- It sets `Type = GameObject` in `BepInEx.cfg` and removes the quarantine attribute.
- It moves an existing `BepInEx` folder to `BepInEx.backup-<date>`.

After the script finishes:

1. Add ValheimCLI and your plugins to `BepInEx/plugins`.
2. Compute `installPins` with `InstallPins.Of("<client install>")`.
3. Set the client section:

```json
"client": {
  "mode": "owned",
  "install": "<client install>",
  "architecture": "arm64",
  "installPins": { "game": "...", "loader": "...", "patchers": "..." }
}
```

Do not start the game with the installer's `play.sh`. `ClientSession.Launch` starts the game itself and owns that process. The repository redistributes none of these binaries.

To see which slice ran, check any of these:

- `client-process.json` and the report's `clientArchitecture`
- BepInEx's log, which reads `System platform: OSX Arm64` in a native process
- `vmmap <pid> | grep "Code Type"`, which shows `ARM64`

### Limits

- Mods with native libraries built only for Intel, Windows or Linux do not load natively.
- Asset bundles without Metal shaders render pink.
- Unusual MonoMod IL hooks may behave differently on MonoMod 25.
- Some newer Macs were reported to need an arm64e Doorstop build.
- Test each mod natively. Keep the x64 path for anything that fails.

## Mac session limits

These limits apply to any owned Mac client, native or not.

The client needs an awake Mac with an unlocked, logged-in desktop session. The display can turn off while that session stays unlocked; macOS Lock Screen settings decide whether that is possible. On a locked console, the client can stall after the first scene and never reach its menu.

If a bundle's code seal is broken, macOS shows the "Valheim is damaged" dialog and kills the game. Adding a file inside `Valheim.app` breaks the seal. Examples are BepInEx preloader logs from a launch from `Contents/MacOS`, or a mod build's `publicized_assemblies`. macOS tolerates this for an install it already let run. For that reason, the profile launches that install's own bundle, never a copy at a new path.

Campaigns and `valheim-test start` check the source bundle, read only, when they prepare its profile:

- `codesign --verify --deep --strict` refuses a bundle with a changed or missing sealed file. To restore it, verify the game's files in Steam. The run never repairs the source.
- Files that were only added inside the bundle are tolerated.
- An intact bundle must also pass `spctl -a -t exec`.
