# Add testing to a mod

Start with the lowest test layer that answers the question. Existing xUnit tests do not need to be rewritten or moved to this repository. Test packages belong in test projects, not in a production mod or a player's plugins directory.

## 1. Get the packages (no Valheim needed)

Use the .NET 10 SDK and Git; no other tooling is needed. From a fresh checkout:

```sh
git clone https://github.com/tvongaza/ValheimTesting.git
cd ValheimTesting
dotnet run scripts/bootstrap-cli.cs
dotnet run scripts/validate.cs
```

The same commands work on Windows, Linux and macOS; CI runs them on all three. What each host can do:

| Host | Toolkit and tests | Game client (`ClientLaunch`, driven by ValheimCLI) | Dedicated server (native) | Server in a container | Remote server host |
|---|---|---|---|---|---|
| Windows | Yes | Yes, `valheim.exe` | Yes | Linux image; not tested on Windows | Coming next (SSH, host profiles) |
| Linux | Yes | Yes, `valheim.x86_64`; needs a display | Yes | Yes, verified | Coming next (SSH, host profiles) |
| macOS | Yes | Yes, `Valheim.app`; x86_64 under Rosetta; native arm64 needs a universal Doorstop | **No**: there is no macOS dedicated server | Experimental (x86-64 emulation on Apple Silicon) | Recommended; coming next (SSH, host profiles) |

Bootstrap fetches the exact ValheimCLI commit in [`cli-dependency.json`](../cli-dependency.json); it does not use whichever checkout happens to be nearby. Validation runs library tests, builds all examples, runs the no-game examples, and creates `.packages/`. No Unity, game files, Steam login or running server is needed.

The toolkit packages, all on NuGet.org:

| Package | Exact version | Use |
|---|---|---|
| `Valheim.Testing` | `0.1.0-preview.6` | Composable terrain, zone state, recorded-input replay and scoped static overrides; no ValheimCLI dependency |
| `Valheim.Testing.Game` | `0.1.0-preview.11` | External game observations, owned sessions, comparisons and reports |
| `Valheim.Testing.Cli` | `0.1.0-preview.5` | ValheimCLI's client transport, packaged from pinned ValheimCLI source; consumed by the Game package |
| `Valheim.Testing.Doubles` | `0.1.0-preview.4` | Unity/Valheim/BepInEx/Jotunn doubles as source, so a unit-test project compiles the mod's pure-logic files without the game (see [Doubles](testing-toolkit.md#game-doubles)) |

Versions need not match each other. They restore from NuGet.org with no extra setup. To try an unpublished build instead, add the local `.packages` feed alongside NuGet.org, which still supplies xUnit and ordinary dependencies. For example, from your mod checkout:

```sh
dotnet restore path/to/MyMod.Tests.csproj -p:RestoreAdditionalProjectSources=/absolute/path/ValheimTesting/.packages
```

`RestoreAdditionalProjectSources` adds the feed and keeps your configured NuGet.org source. Avoid passing NuGet.org as a second `--source`: with .NET SDK 10.0.401 on Windows, restore treated that URL as a local folder and failed.

Pin only the package your test project needs:

```xml
<!-- Pure test project; not the production mod project. -->
<PackageReference Include="Valheim.Testing" Version="[0.1.0-preview.6]" />
<!-- A separate external system-test project instead uses: -->
<PackageReference Include="Valheim.Testing.Game" Version="[0.1.0-preview.11]" />
```

Brackets mean an exact NuGet version. Pure helpers target netstandard2.0; external game tools and examples target net10.0. Keep the game-side plugin's existing target framework.

## Using Visual Studio, Rider or VS Code

The test projects are ordinary SDK-style xUnit projects, so IDE test runners discover them with no extra setup. The external tools and tests target net10.0, which needs an IDE that supports .NET 10:

| IDE | Where tests appear | Notes |
|---|---|---|
| Visual Studio 2026 (Windows) | Test Explorer | Install the **.NET desktop development** workload; it includes the .NET Framework 4.8 targeting pack used by net48 test legs, which run natively on Windows. |
| JetBrains Rider (Windows, macOS, Linux) | Unit Tests window | net48 test legs need Mono on macOS/Linux. |
| VS Code with C# Dev Kit | Testing panel | Same .NET 10 SDK requirement. |

Open the mod's **testing solution** rather than its main solution: `ProceduralRoads.Testing.slnx` or `MoreWorldLocations.Testing.slnx`. It contains the unit tests, the external runner and the runner's tests. Packages restore from NuGet.org automatically.

Two things the command line passes explicitly are set another way in an IDE:

- **Game install path.** Mod projects read Valheim's assemblies from the default Steam folder. If your Steam library is elsewhere, set a `VALHEIM_INSTALL` environment variable before starting the IDE, where the project supports it (the test adapters do), or follow the mod's own build instructions.
- **The game-side test adapter** (`*.TestAdapter`) is deliberately not in the testing solution: it must compile against the exact ValheimCLI build you install. Build it from a terminal with `-p:CliDll=...`, or set a `CliDll` environment variable before starting the IDE.

External runners are console programs. To run one from the IDE, set its command-line arguments in the project's debug or run settings (Visual Studio: project **Properties → Debug → Open debug launch profiles UI**; Rider: **Run → Edit Configurations**). Native runs still need a disposable game install, world and character.

## 2. Keep broad coverage in unit tests

Use [`NoGameTerrain`](../examples/NoGameTerrain/README.md) first. Its executable checks hand-derived heights on a plane, replays captured inputs, and refuses a missing biome instead of inventing one.

In your own tests, feed these small terrain inputs into the real mod decisions. Keep expectations independent of the algorithm under test. A plane or replay is not a replacement for Valheim's generator, Unity physics, native save encoding or networking.

Roads demonstrates gradual adoption: its [SyntheticWorld adapter](https://github.com/tvongaza/ProceduralRoads/blob/review/testing-adoption-ready/ProceduralRoads.Tests/SyntheticWorld.cs) delegates to shared terrain while retaining the existing game doubles and xUnit assertions. Roads-specific tests remain in Roads. Sharing a helper does not require relocating the whole suite.

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

This is an illustrative **client** file, not usable pins. Include every additional loaded plugin, such as ScriptEngine or other packs. On the server use the real mod/adapter hashes instead of `absent`. SHA-256 input manifests and plugin MD5 expectation pins serve different purposes; do not substitute one for the other. See [ValheimCLI expectations](https://github.com/tvongaza/valheimCLI/blob/review/cli-command-packs-ready/README.md#know-what-you-are-testing).

Wait for the world and required zone to be loaded (on events where they exist, see [Waiting](testing-toolkit.md#waiting)), arrange arrival/protection separately, and verify the client's actual position. A responsive ValheimCLI is not proof that world loading has finished. Missing maps or incomplete observations are failures, not zero-height or black-paint measurements.

For persistence, use the **same** independently declared plan before and after a confirmed save, server restart and client rejoin. Require `cli_save`'s completion result before stopping. Run a discriminating negative expectation too: unchanged pre-road paint should fail painted samples while untouched samples still pass. Never widen tolerances merely to make a fixture pass.

The [Roads scenario guide](https://github.com/tvongaza/ProceduralRoads/blob/review/testing-adoption-ready/ProceduralRoads.SystemTests/README.md) shows owned process/copy setup, manifests, preparation versus acceptance, empty-save, bridge respawn and native terrain/paint. MWL's [adapter guide](https://github.com/tvongaza/MoreWorldLocations_All/blob/review/testing-adapter-ready/MoreWorldLocations.TestAdapter/README.md) keeps its port probes separate; those require full mode and their bounded full-mode payment/delivery/ownership acceptance now passes.

### On Linux, macOS or in a container

The dedicated server also runs on Linux. Build the launch with `ServerLaunch.CreateStartInfo(runtime, arguments, environment)` (`Valheim.Testing.Game` `0.1.0-preview.11`) and pass it to `DirectServerProcess` as on Windows. It detects the platform from the runtime's executable, refuses a runtime with both or neither, and on Linux sets BepInEx's Doorstop variables and prepends to `LD_LIBRARY_PATH`/`LD_PRELOAD` without dropping existing entries. The [Linux image](../docker/linux-server/README.md) installs the free dedicated server with anonymous SteamCMD and BepInEx at build time; [LinuxServerSmoke](../examples/LinuxServerSmoke/README.md) is the smallest runner for it.

What works: owned dedicated-server native checks on Linux, locally or in CI; the image and smoke were verified on a Linux x86-64 Docker host on 28 September 2026. Not yet: a game client in the cloud or a container, and remote hosts over SSH. The image contains game files; keep it local or inside the CI job and never publish it.

On macOS, run the toolkit, tests and a Mac game client locally, and put the dedicated server on a Windows or Linux machine. There is no macOS dedicated server: `ServerLaunch` throws `PlatformNotSupportedException` on a macOS host rather than executing a Linux or Windows binary, and also when given a `Valheim.app` client as the server runtime. `Detect` still works on a Mac, so a runtime can be checked before it is copied elsewhere. The Linux image builds on Apple Silicon with `--platform linux/amd64`, but only under emulation; see its [Apple Silicon notes](../docker/linux-server/README.md#apple-silicon-experimental) and treat it as experimental.

## 5. Read the result, then keep human judgement separate

Most comparison examples take a new output directory and write `result.json`, `junit.xml`, command transcripts and residuals. Exit 0 establishes only that tool's assertions. The example READMEs describe their outputs and effects. Inspect game logs too; a passing scenario does not certify every loaded mod.

[WalkingReview](../examples/WalkingReview/README.md) records a person moving normally. A qualifying trace still needs a human verdict on usability and appearance. Small cosmetic bumps can be accepted; a test need not demand a perfect road.

Attachment examples never claim or restore the machine and do not own an existing game process. Owned session tools stop only processes they started; their disposable copies and reports remain for inspection. The operator owns machine/account coordination, backups, protection and restoration. Review reports for private account/world data before publishing.

For composable slopes, cliffs and terraces plus independent per-zone height/paint state, use the [shared-world guide](shared-world.md). Keep game-type shims and writer assertions in your mod.

## Strict calls from tests

Use `valheim-cli --expect-strict /private/pins.txt ...` when invoking the executable. Its flag is **`--expect-strict`**, not a global `--strict`. YAML plans can instead set `game.expect` and `game.expectStrict: true`. The underlying game command is `cli_expect --strict key=value ...`.

For library consumers, `GameActor.VerifyEnvironment` validates and normalizes supplied expectations to strict mode even if the caller omitted the switch. Every subsequent command rechecks those same pins before dispatch; drift invalidates the actor and blocks the action. `Execute(..., requireSuccess: false)` lets a test inspect an expected command refusal but cannot bypass a pin failure. The transport itself is low-level: use an actor for test actions and observations.

Strict mode rejects unlisted loaded plugins/worlds; it does not make `any` an exact build pin. Supply reviewed full hashes and the intended world identity. The preflight is a separate round trip, not an atomic lock on game state. For dispatch-time enforcement as well, configure the game's existing `[Expectations]` guard; tests never disable it.

Reload tests must provide a pins file and explicitly advance the changed plugin's expected hash using the artifact they intend to install, then require absence on removal. Only `cli_expect --strict` is rechecked while that known transition settles; pass `WaitForEnvironment` the reload's own log line (a `LogWait`) so it rechecks when the plugin has loaded rather than on a timer. Repin after world changes. The owned-server startup identity probe is a narrow read-only bootstrap exception while world loading is incomplete; it checks token/PID/save-root identity, then verifies strict pins before returning an actor.

## Developer loop

For the edit-build-test round of a mod on your own machine, [`tools/dev-loop`](../tools/dev-loop/README.md) has the scripts that moved here from ValheimCLI: `dev-loop` builds the mod, installs its DLL, launches Valheim and runs a test plan in strict mode with the mod's pin replaced by the md5 of the build it just deployed; `pin-mods` snapshots and checks a game's plugin pins; `log-summary.sh` counts the log's warnings and errors. Each comes as a bash script for macOS/Linux and a PowerShell twin for Windows (`log-summary` is folded into `dev-loop.ps1`).

```sh
VALHEIM_CLI=/path/to/valheim-cli VALHEIM_EXPECTATIONS=pins.txt tools/dev-loop/dev-loop.sh MyMod.csproj smoke-plan.yaml
```

```powershell
$env:VALHEIM_CLI = 'C:\path\to\valheim-cli.exe'; $env:VALHEIM_EXPECTATIONS = 'pins.txt'
powershell -ExecutionPolicy Bypass -File tools\dev-loop\dev-loop.ps1 MyMod.csproj smoke-plan.yaml
```

The scripts drive the `valheim-cli` executable, which this repository does not build: take it from a [ValheimCLI](https://github.com/tvongaza/valheimCLI) release or build its `CLI` project, at or after the commit in [`cli-dependency.json`](../cli-dependency.json). ValheimCLI's `examples/smoke-plan.yaml` is a plan to start from. `dev-loop` deploys only to a stopped game; point it at a disposable install, world and character. The [tools README](../tools/dev-loop/README.md) lists the environment variables, exit codes and tests.
