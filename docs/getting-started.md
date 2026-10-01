# Add testing to a mod

For your first adoption, use [Bring your mod](adopting.md) and its complete [mod test example](../examples/ModWithTests/README.md).

Start with the lowest test layer that answers the question. Existing xUnit tests do not need to be rewritten or moved to this repository. Test packages belong in test projects, not in a production mod or a player's plugins directory.

## 1. Get the packages (no Valheim needed)

Use the .NET 10 SDK for the examples. Choose one setup path:

- **Consume released packages:** reference an exact published version in your mod's test project and restore from NuGet.org. No framework checkout, Git or ValheimCLI bootstrap is needed for pure tests.
- **Try an unpublished pure helper or doubles build:** pack only `Valheim.Testing` and `Valheim.Testing.Doubles` into a local feed and add it as described in [package versions and feeds](#package-versions-and-feeds). No ValheimCLI dependency is built.
- **Develop this framework or use unpublished Game APIs:** clone the framework, then follow [the contributor bootstrap and validation](../CONTRIBUTING.md#set-up-and-validate-locally). That path builds pinned ValheimCLI transport source and all examples without launching a game.

For supported hosts and runtime requirements, see the [platform table](../README.md#platforms). Native clients still need a display/Steam session (including in the Linux client container); native servers need a supported dedicated-server runtime.

### Package versions and feeds

The toolkit packages, all on NuGet.org:

| Package | Exact version | Use |
|---|---|---|
| `Valheim.Testing` | `0.1.0-preview.7` | Composable terrain, zone state, recorded-input replay and scoped static overrides; no ValheimCLI dependency |
| `Valheim.Testing.Game` | `0.1.0-preview.17` | External game observations, owned sessions, comparisons and reports |
| `Valheim.Testing.Cli` | `0.1.0-preview.5` | ValheimCLI's client transport, packaged from pinned ValheimCLI source; consumed by the Game package |
| `Valheim.Testing.Adapter` | `0.1.0-preview.3` | Source for a mod's game-side test adapter plugin: registration with ValheimCLI and the owned-session identity (see [adapter helpers](testing-toolkit.md#game-side-adapter-helpers-valheimtestingadapter-preview-1)) |
| `Valheim.Testing.Doubles` | `0.1.0-preview.7` | Unity/Valheim/BepInEx/Jotunn doubles as source, so a unit-test project compiles the mod's pure-logic files without the game (see [Doubles](testing-toolkit.md#game-doubles)) |
| `Valheim.Testing.Bindings` | `0.1.0-preview.1` | Library: checks offline that a built mod's references into the game assemblies still bind, and names the mod methods that use each missing member (see [the binding check](testing-toolkit.md#offline-binding-check-valheimtestingbindings-preview-1)) |
| `Valheim.Testing.Bindings.Tool` | `0.1.0-preview.1` | The same check as the `valheim-bindings` .NET tool, for a mod's CI; not a project reference |

Versions need not match each other. They restore from NuGet.org with no extra setup. To try an unpublished build instead, add the local `.packages` feed alongside NuGet.org, which still supplies xUnit and ordinary dependencies. For example, from your mod checkout:

```sh
dotnet restore path/to/MyMod.Tests.csproj -p:RestoreAdditionalProjectSources=/absolute/path/ValheimTesting/.packages
```

`RestoreAdditionalProjectSources` adds the feed and keeps your configured NuGet.org source. Avoid passing NuGet.org as a second `--source`: with .NET SDK 10.0.401 on Windows, restore treated that URL as a local folder and failed.

Pin only the package your test project needs:

```xml
<!-- Pure test project; not the production mod project. -->
<PackageReference Include="Valheim.Testing" Version="[0.1.0-preview.7]" />
<!-- A separate external system-test project instead uses: -->
<PackageReference Include="Valheim.Testing.Game" Version="[0.1.0-preview.17]" />
<!-- A game-side test adapter plugin compiles the adapter source: -->
<PackageReference Include="Valheim.Testing.Adapter" Version="[0.1.0-preview.3]" PrivateAssets="all" />
<!-- A test that checks a built mod DLL against the game's assemblies in code: -->
<PackageReference Include="Valheim.Testing.Bindings" Version="[0.1.0-preview.1]" />
```

Brackets mean an exact NuGet version. Pure helpers target netstandard2.0; external game tools and examples target net10.0. Keep the game-side plugin's existing target framework.

The binding check needs no project reference in most mods. Install the tool in the CI job that builds the plugin and run it on the built DLL against the game's `Managed` directory ([CI step](testing-toolkit.md#offline-binding-check-valheimtestingbindings-preview-1)):

```sh
dotnet tool install Valheim.Testing.Bindings.Tool --version 0.1.0-preview.1 --tool-path .tools
.tools/valheim-bindings MyMod/bin/Release/MyMod.dll --game-dir "path/to/valheim_Data/Managed"
```

Reference the `Valheim.Testing.Bindings` library only from a test that calls `BindingCheck.Check` itself, for example to assert on the report. Neither belongs in the production plugin project, and both need the game's assemblies, so neither runs in a CI job without them.

## Using Visual Studio, Rider or VS Code

The test projects are ordinary SDK-style xUnit projects, so IDE test runners discover them with no extra setup. The external tools and tests target net10.0, which needs an IDE that supports .NET 10:

| IDE | Where tests appear | Notes |
|---|---|---|
| Visual Studio 2026 (Windows) | Test Explorer | Install the **.NET desktop development** workload; it includes the .NET Framework 4.8 targeting pack used by net48 test legs, which run natively on Windows. |
| JetBrains Rider (Windows, macOS, Linux) | Unit Tests window | net48 test legs need Mono on macOS/Linux. |
| VS Code with C# Dev Kit | Testing panel | Same .NET 10 SDK requirement. |

Open your test project directly or add it to your own solution. A separate testing solution is optional: Roads and MWL use `ProceduralRoads.Testing.slnx` and `MoreWorldLocations.Testing.slnx` to group unit tests and external runners. You do not need those files in another mod. Published packages restore from NuGet.org; unpublished candidates need the local feed above.

Two things the command line passes explicitly are set another way in an IDE:

- **Game install path.** Mod projects read Valheim's assemblies from the default Steam folder. If your Steam library is elsewhere, set a `VALHEIM_INSTALL` environment variable before starting the IDE, where the project supports it (the test adapters do), or follow the mod's own build instructions.
- **The game-side test adapter** (`*.TestAdapter`) is deliberately not in the testing solution: it must compile against the exact ValheimCLI build you install. Build it from a terminal with `-p:CliDll=...`, or set a `CliDll` environment variable before starting the IDE.

External runners are console programs. To run one from the IDE, set its command-line arguments in the project's debug or run settings (Visual Studio: project **Properties → Debug → Open debug launch profiles UI**; Rider: **Run → Edit Configurations**). Native runs still need a disposable game install, world and character.

## 2. Keep broad coverage in unit tests

For a test of actual mod source, start with [`ModWithTests`](../examples/ModWithTests/README.md). For an introduction to independent terrain expectations, use [`NoGameTerrain`](../examples/NoGameTerrain/README.md). Its executable checks hand-derived heights on a plane, replays captured inputs, and refuses a missing biome instead of inventing one.

In your own tests, feed these small terrain inputs into the real mod decisions. Keep expectations independent of the algorithm under test. A plane or replay is not a replacement for Valheim's generator, Unity physics, native save encoding or networking.

Roads demonstrates gradual adoption: its [SyntheticWorld adapter](https://github.com/tvongaza/ProceduralRoads/blob/review/testing-adoption-ready/ProceduralRoads.Tests/SyntheticWorld.cs) delegates to shared terrain while keeping mod-specific tests and xUnit assertions in the mod repository. Shared game types can come from `Valheim.Testing.Doubles`; keep mod-specific extensions local. Roads-specific tests remain in Roads. Sharing a helper does not require relocating the whole suite.

## 3. Test orchestration without a game

Use controlled transports to exercise wrong pins, incomplete replies, stale extension instances, timeouts and cleanup. Examples of these tests live in [`tests/Valheim.Testing.Tests`](../tests/Valheim.Testing.Tests). These test the driver and its failure handling; they do not prove that a mod saves correctly in the game.

Put mod-specific assertions in the mod repository. Generic observations, session ownership and report writing belong here; transport and the extension API belong in ValheimCLI. Do not copy infrastructure into every mod.

## 4. Add a small native check where it matters

Use the [example index](../examples/README.md) to choose an observer. Terrain has distinct layers: raw generator height, loaded heightmap, that map's collider, player support, and paint mask. Select the layer that answers the test question. A generator-height match says nothing about a client's loaded collider.

For the current split ValheimCLI build, a typical disposable Roads fixture installs:

| Component | Dedicated server | ValheimCLI-only observation client |
|---|---|---|
| Matching ValheimCLI core | Yes, in plugins | Yes, in plugins |
| Standard pack | Save/session commands | Character/session/protection commands |
| World Tools pack | Terrain/world observations | Terrain/collider/paint/player observations |
| Roads and optional Roads test adapter | Yes | Absent, explicitly pinned |
| Reflection pack | Only if the driver needs `cli_call` | Optional |
| Capture pack | Optional | Only for clutter controls |
| ScriptEngine | Only for reload tests | Only for reload tests |

A clean test runtime is BepInEx core plus these plugins, with an empty `BepInEx/patchers` folder: a preloader patcher left behind by a removed mod breaks the game's types before any plugin loads. The pinned runner refuses patchers its plan does not name. For the plugin dependency and load-order concept, see the wiki's [BepInEx dependencies](https://github.com/Valheim-Modding/Wiki/wiki/Best-Practices#bepinex-dependencies-and-incompatibilities); use the [runtime hygiene checklist](runtime-hygiene.md#plugins) for this toolkit's tested rules.

Core stays in plugins. Put each optional pack in plugins **or** scripts, never both. See the ValheimCLI [pack installation and ownership guide](https://github.com/tvongaza/valheimCLI/blob/review/cli-command-packs-ready/docs/command-packs.md). An older monolithic ValheimCLI and extracted packs cannot be mixed.

Prepare private test settings, an independently specified fixture and a disposable character. Keep credentials out of committed plans and reports. Use `cli_manifest` to inspect actual loaded plugin hashes, `cli_world` for world identity, and `cli_extensions` to confirm the required capabilities. A strict pins file uses one `key=value` per line:

```text
worlduid=YOUR_FIXTURE_WORLD_UID
valheimCLI.valheimCLI=ACTUAL_CORE_MD5
valheimCLI.standard=ACTUAL_STANDARD_MD5
valheimCLI.worldtools=ACTUAL_WORLD_TOOLS_MD5
warpalicious.ProceduralRoads=absent
warpalicious.More_World_Locations_AIO=absent
```

This is an illustrative **client** file, not usable pins. Include every additional loaded plugin, such as ScriptEngine or other packs. On the server use the real mod/adapter hashes instead of `absent`. SHA-256 input manifests and plugin MD5 expectation pins serve different purposes; do not substitute one for the other. `cli_expect` cannot see the game build or BepInEx itself; the pinned runner pins those on disk (`runtimePins`, a client's `installPins`), and `"pinning": "none"` is its explicit, reported opt-out: see [Pins and the opt-out](testing-toolkit.md#pins-and-the-opt-out). See [ValheimCLI expectations](https://github.com/tvongaza/valheimCLI/blob/review/cli-command-packs-ready/README.md#know-what-you-are-testing).

Wait for the world and required zone to be loaded (on events where they exist, see [Waiting](testing-toolkit.md#waiting)), arrange arrival/protection separately, and verify the client's actual position. A responsive ValheimCLI is not proof that world loading has finished. Missing maps or incomplete observations are failures, not zero-height or black-paint measurements.

For persistence, use the **same** independently declared plan before and after a confirmed save, server restart and client rejoin. Require `cli_save`'s completion result before stopping. Run a discriminating negative expectation too: unchanged pre-road paint should fail painted samples while untouched samples still pass. Never widen tolerances merely to make a fixture pass.

The [Roads scenario guide](https://github.com/tvongaza/ProceduralRoads/blob/review/testing-adoption-ready/ProceduralRoads.SystemTests/README.md) shows owned process/copy setup, manifests, preparation versus acceptance, empty-save, bridge respawn and native terrain/paint. MWL's [adapter guide](https://github.com/tvongaza/MoreWorldLocations_All/blob/review/testing-adapter-ready/MoreWorldLocations.TestAdapter/README.md) keeps its port probes separate; those require full mode and their bounded full-mode payment/delivery/ownership acceptance now passes.

### On Linux, macOS or in a container

The dedicated server also runs on Linux. Build the launch with `ServerLaunch.CreateStartInfo(runtime, arguments, environment)` (Game preview.11 onward) and pass it to `DirectServerProcess` as on Windows. It detects the platform from the runtime's executable, refuses a runtime with both or neither, and on Linux sets BepInEx's Doorstop variables and prepends to `LD_LIBRARY_PATH`/`LD_PRELOAD` without dropping existing entries. The [Linux image](../docker/linux-server/README.md) installs the free dedicated server with anonymous SteamCMD and BepInEx at build time; [LinuxServerSmoke](../examples/LinuxServerSmoke/README.md) is the smallest runner for it.

What works: owned dedicated-server native checks on Linux, locally or in CI; the image and smoke were verified on a Linux x86-64 Docker host on 28 September 2026. The separate [Linux client container](../docker/linux-client/README.md) supports native clients on NVIDIA GPU hosts with a display and authenticated Steam session. General remote-host orchestration remains outside the library. The **server** image contains game files; keep it local or inside the CI job and never publish it. The published client base image contains no game files; do not publish it after installing the game.

On macOS the dedicated server runs natively (Game preview 15). Valheim Dedicated Server (Steam app 896660) has a macOS build: install it with anonymous SteamCMD (`steamcmd +force_install_dir <dir> +login anonymous +app_update 896660 validate +quit`; SteamCMD itself runs under Rosetta on Apple Silicon, the server does not). The install is not an app bundle: the server is `valheim_server/Valheim`, a universal (x86_64 and arm64) executable beside Unity's and Mono's libraries and its `Data` folder, and `steamapps/appmanifest_896660.acf` records its build id (25527701, the same as the Windows and Linux servers, on 30 September 2026; check the build ids match before comparing a Mac server with a client on another OS). `ServerLaunch.Detect` returns `MacOS` for such a runtime, and on a Mac `CreateStartInfo` starts it through `/usr/bin/arch` as the machine's own architecture with Doorstop inserted. A macOS server runs only on a Mac, a Mac still runs neither the Windows nor the Linux server, and a `Valheim.app` client is refused as a server runtime.

Modded runs need BepInEx that runs natively on Apple Silicon: BepInExPack_Valheim's Doorstop and core are x86_64 only. Put a `libdoorstop.dylib` with an arm64 slice (UnityDoorstop 4.5 or later; its universal build is one way) at the runtime's root and a BepInEx 5.4.23.5 core rebuilt against HarmonyX and MonoMod releases with arm64 support in `BepInEx/core`, the same stack as the [native Mac client](#native-apple-silicon-client) (community builds, not upstream BepInEx); `CreateStartInfo` refuses a runtime whose server or Doorstop lacks the machine's slice. The server first looks for `steamclient.dylib` beside itself and then used the installed Steam client's; on a Mac without the Steam client, SteamCMD's own `steamclient.dylib` is universal but has not been tried. Remote macOS server hosts (`--profile`) are not supported yet. The Linux image also builds on Apple Silicon with `--platform linux/amd64`, but only under emulation; see its [Apple Silicon notes](../docker/linux-server/README.md#apple-silicon-experimental).

### Native Apple Silicon client

The macOS game client is universal (`lipo -info <install>/valheim.app/Contents/MacOS/Valheim` lists `x86_64 arm64`), and a modded client can run either way. Rosetta is the compatibility path, not the only modded one:

| `client.architecture` | Runs as | Install needs | Use it when |
|---|---|---|---|
| left out, or `"x64"` | x86_64 under Rosetta | BepInExPack_Valheim as installed: `doorstop_libs/libdoorstop_x64.dylib` and its core | Default. Every Mac and every mod that works on Intel Macs; slower on Apple Silicon |
| `"arm64"` (Game preview 15) | native arm64 | a `libdoorstop.dylib` with an **arm64 slice** at the install's root (UnityDoorstop 4.5 or later; a universal or an arm64-only build both have it) and a BepInEx core built on MonoMod 25 or later | Apple Silicon, with mods that have no Intel-only native parts |

The core matters as much as the loader. BepInExPack_Valheim's core uses legacy MonoMod (before 25), which cannot apply Harmony hooks on arm64, where a page is writable or executable but never both. A native core is BepInEx 5.4.23.5 rebuilt on HarmonyX 2.16.1 and MonoMod 25 ([source branch](https://github.com/bbauti/BepInEx/tree/codex/macos-arm64-valheim)); it is a community build, not an upstream BepInEx release. `ClientLaunch` checks both before anything starts: an arm64 plan whose game or Doorstop library has no arm64 slice, or whose `BepInEx/core/MonoMod.RuntimeDetour.dll` is missing or older than 25, is refused with the reason. `ClientRunPlan.Validate` runs the same check on an install on this machine, so a runner's `validate` refuses such a plan and `run` refuses it before it starts the server. It never falls back to Rosetta: the game starts through `/usr/bin/arch -arm64`, which fails rather than run another slice. Windows and Linux clients are x64 only, so `ClientRunPlan.Validate` refuses `arm64` for one, and a remote profile client (Windows or Linux) refuses it too.

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
  "installPins": { "game": "...", "bepinexCore": "...", "patchers": "..." }
}
```

Do not start the game with the installer's `play.sh`; `ClientSession.Launch` starts it itself (the same `arch -arm64` launch with Doorstop inserted) and owns that process. The repository redistributes none of these binaries. `client-process.json` and the report's `clientArchitecture` record which slice ran; BepInEx's log reads `System platform: OSX Arm64` in a native process, and `vmmap <pid> | grep "Code Type"` shows `ARM64`.

Status and limits: the owned `ClientSession` launch with `"architecture": "arm64"` passed FullLifecycle's synced-config scenario on an Apple M2 (macOS 26.5, Valheim 1.0.16, a native macOS dedicated server of the same version) on 30 September 2026 ([#113](https://github.com/tvongaza/ValheimTesting/pull/113)): the client process's code type was ARM64, BepInEx 5.4.23.5 logged `System platform: OSX Arm64` on MonoMod.RuntimeDetour 25.3.4, ValheimCLI with its Standard and WorldTools packs, MyMod and its test adapter loaded with exact pins, strict commands replied, and the client joined with a disposable local character, read the synced greeting, rejoined after a confirmed save and a server-only restart with the saved value, and stopped cleanly. Arm64 plans against an install with the pack's MonoMod 22 core or without an arm64 Doorstop were refused before launch. This is one machine and one game build; other chips, macOS versions and mods are unchecked. The unit tests cover the plan field, slice and core selection and every refusal with synthetic installs. Mods with native libraries built only for Intel, Windows or Linux will not load natively; asset bundles without Metal shaders render pink; unusual MonoMod IL hooks may behave differently on MonoMod 25; and some newer Macs were reported to need an arm64e Doorstop build. Test each mod natively before relying on it, and keep the x64 path for anything that fails.

Two Mac session limits apply to any owned Mac client, native or not. The client needs an unlocked, logged-in desktop session with the display on: on a locked console it stalls after the first scene and never reaches its menu. And the first launch of an install copied to a new path waits on macOS Gatekeeper's first-launch prompt: the process sits suspended until someone at the Mac answers it, so answer that prompt for each new copy before relying on unattended runs.

## 5. Read the result, then keep human judgement separate

Most comparison examples take a new output directory and write `result.json`, `junit.xml`, command transcripts and residuals. Exit 0 establishes only that tool's assertions. The example READMEs describe their outputs and effects. Inspect game logs too; a passing scenario does not certify every loaded mod.

[WalkingReview](../examples/WalkingReview/README.md) records a person moving normally. A qualifying trace still needs a human verdict on usability and appearance. Small cosmetic bumps can be accepted; a test need not demand a perfect road.

Attachment examples never claim or restore the machine and do not own an existing game process. Owned session tools stop only processes they started; their disposable copies and reports remain for inspection. The operator owns machine/account coordination, backups, protection and restoration. Review reports for private account/world data before publishing.

For composable slopes, cliffs and terraces plus independent per-zone height/paint state, use the [shared-world guide](shared-world.md). Use the shared [game doubles, world scope and terrain assertions](testing-toolkit.md#game-doubles) where they model the behavior you need. Keep mod-specific extensions, expected results and scenario tests in your mod.

## Strict calls from tests

Use `valheim-cli --expect-strict /private/pins.txt ...` when invoking the executable. Its flag is **`--expect-strict`**, not a global `--strict`. YAML plans can instead set `game.expect` and `game.expectStrict: true`. The underlying game command is `cli_expect --strict key=value ...`.

For library consumers, `GameActor.VerifyEnvironment` validates and normalizes supplied expectations to strict mode even if the caller omitted the switch. Every subsequent command rechecks those same pins before dispatch; drift invalidates the actor and blocks the action. `Execute(..., requireSuccess: false)` lets a test inspect an expected command refusal but cannot bypass a pin failure. The transport itself is low-level: use an actor for test actions and observations.

Strict mode rejects unlisted loaded plugins/worlds; it does not make `any` an exact build pin. Supply reviewed full hashes and the intended world identity. The preflight is a separate round trip, not an atomic lock on game state. For dispatch-time enforcement as well, configure the game's existing `[Expectations]` guard; tests never disable it.

Reload tests must provide a pins file and explicitly advance the changed plugin's expected hash using the artifact they intend to install, then require absence on removal. Only `cli_expect --strict` is rechecked while that known transition settles; pass `WaitForEnvironment` the reload's own log line (a `LogWait`) so it rechecks when the plugin has loaded rather than on a timer. Repin after world changes. The owned-server startup identity probe is a narrow read-only bootstrap exception while world loading is incomplete; it checks token/PID/save-root identity, then verifies strict pins before returning an actor.

## Developer loop

For the edit-build-test round of a mod on your own machine, [`tools/dev-loop`](../tools/dev-loop/README.md) has the scripts that moved here from ValheimCLI: `dev-loop` builds the mod, installs its DLL, launches Valheim and runs a test plan in strict mode with the mod's pin replaced by the md5 of the build it just deployed; `pin-mods` snapshots and checks a game's plugin pins; `log-summary.sh` counts the log's warnings and errors; `world-hash` proves which saved world a game loaded; `sample-value` records a value over time as CSV. Each comes as a bash script for macOS/Linux and a PowerShell twin for Windows (`log-summary` is folded into `dev-loop.ps1`).

```sh
VALHEIM_CLI=/path/to/valheim-cli VALHEIM_EXPECTATIONS=pins.txt tools/dev-loop/dev-loop.sh MyMod.csproj tools/dev-loop/smoke-plan.yaml
```

```powershell
$env:VALHEIM_CLI = 'C:\path\to\valheim-cli.exe'; $env:VALHEIM_EXPECTATIONS = 'pins.txt'
powershell -ExecutionPolicy Bypass -File tools\dev-loop\dev-loop.ps1 MyMod.csproj tools\dev-loop\smoke-plan.yaml
```

The scripts drive the `valheim-cli` executable, which this repository does not build: take it from a [ValheimCLI](https://github.com/tvongaza/valheimCLI) release or build its `CLI` project, at or after the commit in [`cli-dependency.json`](../cli-dependency.json). [`tools/dev-loop/smoke-plan.yaml`](../tools/dev-loop/smoke-plan.yaml) is a strict plan to start from. `dev-loop` deploys only to a stopped game; point it at a disposable install, world and character. The [tools README](../tools/dev-loop/README.md) lists the environment variables, exit codes and tests.
