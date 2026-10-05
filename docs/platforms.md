# Platforms

*Assistant-written (Claude).*

The usual setup is one **Windows** PC with Valheim installed through Steam: unit tests, the integration tests and the native checks all run there, with an owned dedicated server and an owned client on the same machine ([one Windows PC](packages/Valheim.Testing.Game.md#on-one-windows-pc)). Everything below is for other hosts, and none of it is needed for that setup. The toolkit itself needs only the .NET 10 SDK on every host, and CI runs bootstrap and validation on Windows, Linux and macOS.

| Host | Toolkit and tests | Game client (`ClientLaunch`, driven by ValheimCLI) | Dedicated server (native) | Server in a container | Remote server host |
|---|---|---|---|---|---|
| Windows | Yes; CI | Yes, `valheim.exe` | Yes, `valheim_server.exe` | Linux image; not tested on Windows | SSH game host in an environment inventory; a client starts in its desktop session, while a headless dedicated server starts from a disposable runtime copy without Steam or a desktop session |
| Linux | Yes; CI | Yes, `valheim.x86_64`; needs a display, a GPU and a signed-in Steam client the operator provides | Yes, `valheim_server.x86_64` | Yes, [docker/linux-server](../docker/linux-server/README.md); its smoke verified on a Linux x86-64 Docker host (server build 25527701) | SSH game host in an environment inventory; natively, a server-only run on a Linux VM over SSH |
| macOS | Yes; CI | Yes, `Valheim.app`, modded either way: x86_64 under Rosetta (the default, BepInExPack_Valheim as installed) or native arm64 with `"architecture": "arm64"`, a Doorstop with an arm64 slice and a BepInEx core on MonoMod 25 ([native client](#native-apple-silicon-client)) | Yes, `valheim_server/Valheim` from Steam app 896660's macOS build, native arm64; modded needs a Doorstop with an arm64 slice and a natively running BepInEx core ([macOS](#macos)) | Experimental: x86-64 emulation on Apple Silicon | Recommended: SSH game host in an environment inventory; natively, a server-only run on a Linux VM over SSH from macOS |

`ClientLaunch` builds a BepInEx game-client launch (Doorstop loader checks and variables, `-console`, `SteamAppId`) from a client install on its own OS, and `GameLaunch.ForServer` does the same for a dedicated server; each refuses the other's install and names the right one ([launching servers and clients](packages/Valheim.Testing.Game.md#launching-servers-and-clients)). A client needs an interactive desktop session with a display and a running Steam client, so `InteractiveClient` starts one inside a Windows or Linux host's existing desktop session ([clients in a host's desktop session](packages/Valheim.Testing.Game.md#clients-in-a-hosts-desktop-session)).

## Windows

A game client runs as `valheim.exe` and a dedicated server as `valheim_server.exe`, both from Steam installs (the server is the free Valheim Dedicated Server tool, app 896660). The owned client needs the Steam client running and signed in in the same desktop session; the dedicated server does not. Another Windows machine can host the server over SSH: a headless server starts from a disposable runtime copy without Steam or a desktop session ([a server-only plan on a remote host](packages/Valheim.Testing.Game.md#a-server-only-plan-on-a-remote-host)).

## Linux and containers

Owned dedicated-server checks can run on Linux as well as Windows. `ServerLaunch.Detect` reads a copied server runtime's platform from its executable (`valheim_server.exe` or `valheim_server.x86_64`) and `GameLaunch.ForServer` builds the direct launch that `DirectServerProcess` and `OwnedServerSession` own, including BepInEx's Doorstop variables on Linux. [docker/linux-server](../docker/linux-server/README.md) builds a local x86-64 image with the server, BepInEx and .NET 10, and the manual [Linux workflow](../.github/workflows/native-linux.yml) boots it once with [LinuxServerSmoke](../docker/linux-server/smoke/README.md). The [scheduled server checks](../docker/linux-server/README.md#scheduled-checks-in-ci) run that smoke and the [FullLifecycle](../examples/FullLifecycle/README.md#the-server-half-alone) example's server half every night and after each new public server build, and keep one tracking issue open while they fail. On Apple Silicon the image runs only under emulation and remains experimental.

Linux game clients run on a host whose desktop session (display, GPU, signed-in Steam client) the operator provides; the toolkit no longer ships a client container (#230). An [environment inventory and game hosts](packages/Valheim.Testing.Game.md#game-hosts-and-the-environment-inventory) (local, SSH, container) run scripts, take a host lock, ship a revision, follow a log, fetch evidence and reach a loopback-only ValheimCLI through an SSH tunnel. `PinnedServerRun --inventory` runs a plan's dedicated server on an inventory host (Linux over SSH or in a container, or Windows), from any OS ([a server-only plan on a remote host](packages/Valheim.Testing.Game.md#a-server-only-plan-on-a-remote-host)); a campaign (`PinnedServerRun.RunCampaignAsync`) places remote clients too, each started in its host's desktop session (`InteractiveClient`) on the Steam identity signed in there, leased so no two runs use it at once. A server-only run from macOS over SSH to a Linux VM has passed natively, and a Windows client was started in its desktop session over SSH during a crossplay run; provisioning the host and signing in its Steam client stays with the operator.

Server images contain Valheim's game files. Build them locally or in the CI job that uses them; never push, upload or publish an image, layer or server directory. The published Linux client base image contains no game files; do not publish it after installing the game.

## macOS

On macOS the dedicated server runs natively. Valheim Dedicated Server (Steam app 896660) has a macOS build: install it with anonymous SteamCMD (`steamcmd +force_install_dir <dir> +login anonymous +app_update 896660 validate +quit`; SteamCMD itself runs under Rosetta on Apple Silicon, the server does not). The install is not an app bundle: the server is `valheim_server/Valheim`, a universal (x86_64 and arm64) executable beside Unity's and Mono's libraries and its `Data` folder, and `steamapps/appmanifest_896660.acf` records its build id; check that the build ids match before comparing a Mac server with a client on another OS. `ServerLaunch.Detect` returns `MacOS` for such a runtime, and on a Mac `CreateStartInfo` starts it through `/usr/bin/arch` as the machine's own architecture with Doorstop inserted. A macOS server runs only on a Mac, a Mac still runs neither the Windows nor the Linux server, and a `Valheim.app` client is refused as a server runtime.

Modded runs need BepInEx that runs natively on Apple Silicon: BepInExPack_Valheim's Doorstop and core are x86_64 only. Put a `libdoorstop.dylib` with an arm64 slice (UnityDoorstop 4.5 or later; its universal build is one way) at the runtime's root and a BepInEx 5.4.23.5 core rebuilt against HarmonyX and MonoMod releases with arm64 support in `BepInEx/core`, the same stack as the [native Mac client](#native-apple-silicon-client) (community builds, not upstream BepInEx); `CreateStartInfo` refuses a runtime whose server or Doorstop lacks the machine's slice. The server first looks for `steamclient.dylib` beside itself and then used the installed Steam client's; on a Mac without the Steam client, SteamCMD's own `steamclient.dylib` is universal but has not been tried. Remote macOS server hosts (`--inventory`) are not supported yet. The Linux image also builds on Apple Silicon with `--platform linux/amd64`, but only under emulation; see its [Apple Silicon notes](../docker/linux-server/README.md#apple-silicon-experimental).

### Native Apple Silicon client

The macOS game client is universal (`lipo -info <install>/valheim.app/Contents/MacOS/Valheim` lists `x86_64 arm64`), and a modded client can run either way. Rosetta is the compatibility path, not the only modded one:

| `client.architecture` | Runs as | Install needs | Use it when |
|---|---|---|---|
| left out, or `"x64"` | x86_64 under Rosetta | BepInExPack_Valheim as installed: `doorstop_libs/libdoorstop_x64.dylib` and its core | Default. Every Mac and every mod that works on Intel Macs; slower on Apple Silicon |
| `"arm64"` | native arm64 | a `libdoorstop.dylib` with an **arm64 slice** at the install's root (UnityDoorstop 4.5 or later; a universal or an arm64-only build both have it) and a BepInEx core built on MonoMod 25 or later | Apple Silicon, with mods that have no Intel-only native parts |

The core matters as much as the loader. BepInExPack_Valheim's core uses legacy MonoMod (before 25), which cannot apply Harmony hooks on arm64, where a page is writable or executable but never both. A native core is BepInEx 5.4.23.5 rebuilt on HarmonyX 2.16.1 and MonoMod 25 ([source branch](https://github.com/bbauti/BepInEx/tree/codex/macos-arm64-valheim)); it is a community build, not an upstream BepInEx release. `ClientLaunch` checks both before anything starts: an arm64 plan whose game or Doorstop library has no arm64 slice, or whose `BepInEx/core/MonoMod.RuntimeDetour.dll` is missing or older than 25, is refused with the reason. `ClientRunPlan.Validate` runs the same check on an install on this machine, so a runner's `validate` refuses such a plan and `run` refuses it before it starts the server. It never falls back to Rosetta: the game starts through `/usr/bin/arch -arm64`, which fails rather than run another slice. Windows and Linux clients are x64 only, so `ClientRunPlan.Validate` refuses `arm64` for one, and a remote campaign client (Windows or Linux) refuses it too.

The default stays x64 because it works with the loader and core every Valheim mod guide installs, on every Mac, and a plan then means the same process wherever it runs; arm64 needs a different core that the toolkit cannot supply. Choose `arm64` explicitly once the install has it.

A short recipe with the community installer [Relokk1/valheim-native-arm64](https://github.com/Relokk1/valheim-native-arm64), pinned to the commit checked here (read `install.sh` before running it; it downloads BepInExPack_Valheim 5.4.2333 from Thunderstore, replaces `BepInEx/core` with its rebuilt core, copies UnityDoorstop 4.5.0's universal `libdoorstop.dylib` to the install's root, sets `Type = GameObject` in `BepInEx.cfg`, removes the quarantine attribute and moves an existing `BepInEx` folder to `BepInEx.backup-<date>`). Use a test install and a disposable local character, never your own:

```sh
git clone https://github.com/Relokk1/valheim-native-arm64.git
cd valheim-native-arm64
git checkout cd5565a82dbff8332989c812cb141ad5638dbd52
./install.sh "<client install>"   # the folder that holds valheim.app
```

Then add ValheimCLI and your plugins to `BepInEx/plugins`, compute `installPins` with `InstallPins.Of("<client install>")`, and set the client section to launch natively:

```json
"client": {
  "mode": "owned",
  "install": "<client install>",
  "architecture": "arm64",
  "installPins": { "game": "...", "loader": "...", "patchers": "..." }
}
```

Do not start the game with the installer's `play.sh`; `ClientSession.Launch` starts it itself (the same `arch -arm64` launch with Doorstop inserted) and owns that process. The repository redistributes none of these binaries. `client-process.json` and the report's `clientArchitecture` record which slice ran; BepInEx's log reads `System platform: OSX Arm64` in a native process, and `vmmap <pid> | grep "Code Type"` shows `ARM64`.

Limits: the native arm64 path has run FullLifecycle's synced-config scenario (join, confirmed save, server-only restart, rejoin, against a native macOS dedicated server) on one Apple M2 with macOS 26.5 and Valheim 1.0.16, with BepInEx 5.4.23.5 on MonoMod.RuntimeDetour 25.3.4 and exact pins ([#113](https://github.com/tvongaza/ValheimTesting/pull/113)); unit tests cover the plan field, slice and core selection and every refusal with synthetic installs; other chips, macOS versions and mods are unchecked. Arm64 plans against an install with the pack's MonoMod 22 core or without an arm64 Doorstop are refused before launch. Mods with native libraries built only for Intel, Windows or Linux will not load natively; asset bundles without Metal shaders render pink; unusual MonoMod IL hooks may behave differently on MonoMod 25; and some newer Macs were reported to need an arm64e Doorstop build. Test each mod natively before relying on it, and keep the x64 path for anything that fails.

Two Mac session limits apply to any owned Mac client, native or not. The client needs an unlocked, logged-in desktop session with the display on: on a locked console it stalls after the first scene and never reaches its menu. And the first launch of an install copied to a new path waits on macOS Gatekeeper's first-launch prompt: the process sits suspended until someone at the Mac answers it, so answer that prompt for each new copy before relying on unattended runs.

