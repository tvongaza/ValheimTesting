# Valheim testing toolkit preview

Assistant-written implementation notes. Preview prototype. The Roads dedicated save/restart and bridge-respawn pilot passed its bounded Valheim checks on 27 September. The shared lifecycle also passed empty-save on the dedicated server and rejected the archived broken Roads build at the reload assertion. The Roads-owned declared terrain fixture now passes all 100 height and server-collider samples in Valheim 1.0.16; omitting its writer fails 48 samples. Persistent natural-input terrain and client collision also passed the bounded gate described below.

## Test pyramid

1. **Broad base: unit and synthetic tests.** Real mod code runs against small explicit doubles and reusable terrain inputs. Fast default CI, no Valheim installation. Test decisions, persistence formats, queues, ownership and failures here.
2. **Smaller integration layer.** Exercise the ValheimCLI protocol/runner, adapter registry, fixture handling and scenario orchestration together with controlled transports. No game required for most of these.
3. **Small system-test cap.** Real dedicated server for save/restart and bridge respawn; one instrumented client for terrain replication/collision; one actual ScriptEngine reload to verify assembly binding. Prepared worlds, a few zones, bounded commands. Record unexecuted cases as pending.
4. **Human judgement.** A short look/walk where appearance or usability matters. Do not turn every visual issue into an exact-coordinate assertion.

A successful fake transport run is an orchestration test, not an in-game pass. An input replay round-trip proves capture plumbing, not the game's terrain implementation. Broader fake physics or copied routing algorithms would weaken this pyramid.

## Components

| Component | Runtime and purpose |
|---|---|
| `valheimCLI.dll` | net48, stable BepInEx plugin: existing commands, transport, broker, extension host |
| `Valheim.Testing.Cli` | net10.0, the same client and YAML runner used by the executable, packaged from ValheimCLI's `Valheim.Cli.Testing` project (source in `CLI/Testing`) at a pinned commit |
| `Valheim.Testing` | netstandard2.0, synthetic plane/island/ridge/river and exact captured-sample replay; works with net48/Mono and modern .NET |
| `Valheim.Testing.Game` | net10.0, named actors, typed observations, event waits (log line, process exit, game state) with bounded fallbacks, fixture copies, comparisons and JSON/JUnit reports |
| Roads pilot | Separate Roads checkout: test-only world adapter, game observation plugin and system scenarios |

The toolkit lives in this repository and consumes the ValheimCLI transport as a pinned NuGet package, `Valheim.Testing.Cli`, built from ValheimCLI source. The upstream ValheimCLI PR should include the client-library split and extension API, not demand ownership of Roads tests. No Unity/game DLL is a toolkit dependency. In-game adapters must not load the external (net10.0) test-side packages.

## Run locally

```sh
dotnet run scripts/bootstrap-cli.cs
dotnet run scripts/validate.cs
```

The Roads pilot checkout has its own existing net10.0/net48 suite, the four extracted-fixture parity tests, and a separate `ProceduralRoads.SystemTests.Tests` suite. Existing test fixtures retain exact synthetic outputs; this requirement is about changing the input model, not demanding identical real-game road shapes.

## Extension API v1

Keep the core in normal `BepInEx/plugins`. Put only the optional test adapter in `BepInEx/scripts` for ScriptEngine reload. Do not load a second CLI/API assembly with the adapter. Core hot replacement during adapter work is not supported: finish work and restart the process. Assembly unload/managed-memory reclamation is not promised.

```csharp
registration = valheimCLIPlugin.Instance.Extensions.Register("my.mod.tests", "0.1.0", 1,
    new ExtensionCommand("snapshot", "Read a complete observation", Snapshot,
        readOnly: true, role: ExtensionRole.Server, needsWorld: true));
// OnDestroy: registration?.Dispose();
```

Registration validates every command before publishing any. Names are namespaced by owner. Each instance has a new token. Dispose removes capabilities immediately and cancels queued/active handlers. Cleanup runs in reverse order after work settles; cleanup failure prevents silent replacement. Handlers run on the main thread, yield `null` to wait a frame, and must return `context.Succeed(data)` or `context.Fail(code, message)`. Arbitrary Unity yield instructions are not supported in v1.

Mutation is the safe default (`readOnly: false`), requires devcommands, and uses the existing async operation gate. Joined-client mutation also requires the existing opt-in client setting. Trusted adapters can of course mislabel or ignore these rules; this is an API contract, not a sandbox. Tests must serialize other synchronous console mutations themselves.

If an effect continues after cancellation, install a `WaitForQuiescence` probe *before* issuing it. The core holds the gate and retiring owner until the probe confirms completion. A throwing/stuck probe leaves the owner blocked rather than claiming successful cleanup; diagnose and restart. The API cannot roll back arbitrary terrain/spawn/save effects.

Use `cli_extensions` to discover commands, instance tokens and result versions. Use `cli_extension owner/command args` over the normal ValheimCLI connection. JSON values are bounded to 256 KiB/16 levels, strings/finite numbers/bools/arrays/string-keyed objects. Unsupported values fail explicitly. The actor rejects stale instance tokens and incomplete measurements. Arguments are single tokens in this preview; adapters validate their own grammar.

The bundled `valheim.world/terrain x z generator|loaded-ground` observation demonstrates reuse. It distinguishes raw generator height from actual loaded heightmap ground. Missing heightmap returns `complete: false` and null height; it does not invent a zero.

## Actors, fixtures and evidence

A `GameActor` wraps the existing transport; it never owns/stops the attached process. Verify `cli_expect` pins before using an actor. Require a capability/version, issue a mutation once, then poll its read-only observation. Unknown outcomes are failures, not permission to resend. Keep the command timeout below the overall scenario allowance: a synchronous read cannot be forcibly interrupted by an observation deadline.

`WorldFixture.Copy` requires a hash manifest, refuses links and unexpected files, verifies copied bytes and creates its own new directory. It deletes only that copy; `Preserve=true` keeps it for debugging. Stop the owned game BEFORE disposing its world fixture. It does not own accounts, characters, ports or remote machine reservations. Those remain explicit session-operator responsibilities in preview 1.

`ScenarioReport` records outcomes and teardown failure in JSON/JUnit. Use a unique output directory per run; include fixture/build/config hashes in `Provenance`. Output may contain player/world data; inspect before publishing. Do not include credentials in fixture configuration or logs.

## Waiting

Wait for the event that announces a change, not for time to pass. Every wait takes an explicit, finite timeout; there is no default. Expiry throws `WaitTimeoutException` (a `TimeoutException`) and an early failure throws `WaitFailedException` (an `InvalidOperationException`). Both report what was awaited, the elapsed time and the last thing seen.

| Source | Wait | Notes |
|---|---|---|
| A log line | `LogWait` | Opens at the file's current end (or a given byte offset), so earlier lines never match; the file may not exist yet. Matches complete lines only, checks optional failure patterns first, and re-reads a truncated or replaced file from its start. A `FileSystemWatcher` wakes the wait; a 2 s re-read covers filesystems that drop watcher events (network shares, container mounts). |
| A process exit | `ProcessWait.ForExitAsync` | Returns the exit code; expiry leaves the process running for its owner to stop. |
| A game state | `StateWait` | ValheimCLI's `SUBSCRIBE_STATE` push on a connection used for nothing else. A dedicated server's loaded world is `InWorldNoPlayer`. A state is not mod readiness. |
| No event | `Check.Eventually` | Bounded fallback: re-observes a read-only source at an interval, for example an adapter's `complete` flag. |

```csharp
using var log = new LogWait(Path.Combine(runtime, "BepInEx", "LogOutput.log")); // before launching
// ... start the server ...
await log.WaitAsync(StartupEvents.CliListening, TimeSpan.FromMinutes(5), [LogWait.Literal("[Fatal")]);
```

`OwnedServerSession` waits on these when given `Events = new StartupEvents { CliLog = ..., States = () => StateWait.Connect(host, port) }`: no connection before this boot's `Command server listening` line, then the world-loaded push, then the session probe. The process exit is always watched: a server that exits during startup fails it at once with its exit code and the last log line. Only the adapter's own readiness, which has no event, is re-probed at the poll interval. Without `Events`, connecting keeps its bounded retries. `GameActor.WaitForEnvironment` takes an event too, for example `(left, token) => log.WaitAsync(loadLine, left, cancellation: token)`: it rechecks the pins when the reload's line appears, not every 200 ms.

`StateWait` limit: ValheimCLI's client exposes no awaitable read for pushes, so `StateWait` checks the bytes it has already received every 100 ms (nothing is sent) and asks for the state every 2 s, which recovers a push the client's own reads can swallow and notices a closed connection.

`IServerProcess` gained `WaitForExitAsync`; custom implementations must add it. Startup and reload expiry now throw the subclasses above, so tests asserting the exact `TimeoutException` or `InvalidOperationException` type need updating.

## Small game validation gate

- Reload probe A→B under ScriptEngine (**passed on Mac Valheim at the main menu, 26 September 2026; see [the repeatable check](../examples/ReloadCheck/README.md)**), with ValheimCLI core stable: change/remove a command, verify discovery and instance changed, retained command answers B, removed command is absent, connection remains usable.
- Roads empty-save scenario on a disposable old-network fixture: confirmed save, owned restart, zero cells/points/crossings, loaded-from-save true.
- Roads pending bridge append→respawn: prove pending before action; compare frozen independent expected pieces with marked ZDOs by full transform and multiplicity; save/restart and compare again.
- One matched-input replay/game boundary-road case and one Roads-absent instrumented-client observation: actual height, area readiness and collision. Preserve unknown/missing observations as incomplete. Use ordinary vanilla terrain for server-only acceptance.

Do not expand this into a full-world matrix per commit. These checks gate the preview release and relevant engine/adapter changes, not every local test iteration.

## Mod-owned adapters and compatibility commands

MWL port/shipment probes now belong to `MoreWorldLocations.TestAdapter` in MWL's
repository, with external assertions in `MoreWorldLocations.SystemTests`. The ValheimCLI
core no longer registers `cli_mwl_*` commands or resolves MWL types. Install the
optional adapter to retain those command names. An older core that still owns
the names is refused by the adapter rather than silently overwritten.

An adapter may register compatibility console commands calling
`ExtensionHost.Execute(registry, path, arguments, output)`. This is the same
dispatcher as `cli_extension`; it does not bypass role, world, devcommands, client
opt-in, cancellation or the mutation gate. The adapter must remove only its own
command instances when disposed. Successful results can include a bounded
`legacyLines` string array to retain established output alongside structured JSON.

Extension mutation checks read the raw devcommands flag. Valheim's
`IsCheatsEnabled()` also requires being the server, and would otherwise reject
every opted-in joined client. Ten pure policy tests cover the distinction and
ensure the opt-in cannot waive world or role restrictions. This does not expand
the waiver for ordinary console commands owned by other plugins.

Client-side follow-up: run an opted-in joined-client mutation and its refused
controls in a disposable fixture. Server Devcommands is a user-suggested reference
for enabling client devcommands if that check exposes a missing game-level step.
No dependency on that mod or general admin-command bypass has been added.


## Shared owned-session lifecycle (preview 2)

`Valheim.Testing.Game` now owns `OwnedServerSession`, `DirectServerProcess` and
`RecordingTransport`. Roads consumes these types instead of copying them. Other
mods provide their own namespaced session capability, for example
`other.mod.tests/session`; the shared package has no Roads dependency.

The launch callback receives a fresh unpredictable token for every boot. Pass it
to your prepared child process, and have your adapter report the exact token,
PID, actual save root, `dedicated` and `complete`, under source
`owned-test-session`. The session requires a valid extension result envelope from
the configured owner, verifies process/save identity, then strict environment
pins. Define `complete` to include your mod's readiness. Startup waits on events
(see [Waiting](#waiting)) and re-probes only incomplete readiness observations, never mutations. Stop/restart owns only that process and
refuses to start a replacement after failed cleanup. Per-boot logs preserve prior
startup evidence. It does not own accounts, machine reservations or external processes.

The previous Roads-local implementation passed dedicated-server checks. The
extraction is covered by fast lifecycle/scenario tests and a second mod owner
fixture. Its dedicated empty-save repeat passes on the fixed Roads build and fails
after reload on the archived broken build. The earlier bridge pass was on the
Roads-local lifecycle; it was not rerun after extraction.

## Terrain checks and clean package examples

`TerrainProbe` validates coordinate identity, metres, completeness and explicit
generator versus loaded-ground layers. It compares up to 256 declared independent
expectations and reports every residual. See `examples/TerrainCheck` for a
read-only game check and `examples/NoGameTerrain` for a package-only, no-game
example with hand-derived plane expectations and explicit unknown replay data.
Neither a replay of its own capture nor a synthetic plane alone establishes
Valheim terrain conversion or client physics. The declared server calibration and native-input client gate below now cover those specific boundaries.

The package examples accept `-p:ToolkitPackageVersion=0.1.0-preview.5` instead of
project references. Copy just an example directory outside this repository, add
the built `.packages/` directory as a NuGet source alongside nuget.org (for
YamlDotNet), restore and run/build with that property. No game, Unity, BepInEx or
Roads assemblies belong in these NuGet packages. They are external test-driver
libraries; do not install them as game plugins.

## First paired terrain fixture

Roads now owns a two-zone declared-platform fixture under its optional test
adapter and system runner. The same analytic expectations exercise its real
writer with minimal doubles and the actual game compiler/heightmap/collider.
All 100 samples matched in game; the no-write control failed exactly the 48
influenced samples. This is the small system-test cap validating the broader
local test layer. It does not establish natural generator fidelity, paint or
Roads-absent client replication; the temporary platform is not sent to a client.
No new shared package or production-mod dependency was required.

## Native terrain replicated to a ValheimCLI-only client (preview 3)

The Roads-owned persistent fixture now captures native pre-write vertices, writes
real saved compiler deltas, and derives expected results independently from a
small declared road profile. `SurfaceProbe` and `examples/ClientSurfaceCheck`
compare loaded heightmap vertices and **their own** mesh colliders separately.
They also require three stationary, grounded local-player observations; correct
position alone, flying, an attachment or an active teleport is insufficient.

On Valheim 1.0.16 a client with only ValheimCLI loaded (Roads/MWL pinned absent) matched
all 15 unique samples exactly, before and after a confirmed save/server restart/
rejoin. A deliberately unchanged-ground expectation failed 8 samples. The earlier
100-sample server calibration and no-write control remain a separate test.
This is a bounded replication/support gate, not paint, arbitrary generator
fidelity, network routing, walking usability, or an MWL port gameplay test.

Preview 3 adds the surface assertions and keeps packages version-aligned. Toolkit
63 tests, ValheimCLI 792 tests, Roads scenarios 58 tests and MWL adapter 35 tests cover
the local layers; Roads real-source tests pass 766/766 on both .NET 10 and Mono.
The optional adapters and shared toolkit stay outside ordinary mod releases.

Important game boundary: the game's achievement/cheat confirmation can refuse
`cli_arrive` even after devcommands is enabled. The transport can still return
that console text as a completed request. Require the command's documented result
or observe its effect independently; do not treat transport completion as a
successful mutation. This campaign used an authorized server peer teleport and
client-owned arrival/support observations, without granting client admin rights.


## Published-library validation update

Current local layers: 94 shared-library tests, 802 ValheimCLI tests, 62 Roads scenario tests and 35 MWL adapter/scenario tests. Roads retains 774 production-source unit tests on .NET 10 and Mono. The Roads unit world delegates to the shared synthetic terrain model; Roads-specific doubles, assertions and scenarios remain in Roads. Normal mod builds do not depend on the test libraries.

The native paint extension has now been exercised on Valheim 1.0.16: sixteen saved RGBA texels across two zones, paved core plus untouched painted verge, alpha preserved, before and after save/server restart/rejoin on a ValheimCLI-only client. The same plan failed exactly eight samples when deliberately given the unchanged pre-road expectation. Height, collider and stationary support checks passed alongside paint. The [follow-up campaign](native-validation-20260927.md) adds native dirt/fading-edge coverage. Rendered appearance and human walking remain follow-ups.

The four-pack layout also passed join and confirmed-save paths. Optional Reflection was removed and reloaded in a loaded dedicated world over the same connection; only its owner identity changed. This establishes lifecycle behavior, not reclamation of loaded assemblies. Native checks exposed a missing Mono verification build setting in the packs, which is corrected in ValheimCLI #40.

## Linux dedicated server (preview 11)

`ServerLaunch` builds an owned dedicated-server launch from a copied runtime directory. The platform comes from the runtime's contents: `valheim_server.exe` is Windows, `valheim_server.x86_64` is Linux, and both or neither is refused. It requires the BepInEx preloader and the platform's Doorstop loader so a broken install is refused instead of starting a vanilla server. On Linux it also requires the executable's user-execute bit (not checked on a Windows host, which has no mode to read) and reproduces what BepInExPack_Valheim's `start_server_bepinex.sh` exports: `DOORSTOP_ENABLED`, `DOORSTOP_TARGET_ASSEMBLY`, `linux64` and `doorstop_libs` prepended to `LD_LIBRARY_PATH`, and `libdoorstop_x64.so` prepended to `LD_PRELOAD`. Existing entries are kept and no empty entry is added. `SteamAppId` defaults to 892970 on both platforms unless the caller sets it. The executable is started directly rather than through the script, so the owned-session PID check is unchanged. Arguments stay the caller's plan.

`docker/linux-server` is a local Ubuntu 24.04 image: .NET 10 SDK, non-root user, the dedicated server from anonymous SteamCMD at build time and the hash-verified BepInEx pack. The manual `native-linux.yml` workflow builds it on the runner and runs `examples/LinuxServerSmoke`, which requires BepInEx's `Chainloader startup complete` and the server log's creation of the generated world followed by `Opened Steam server`, then stops the process. Only logs and the report are uploaded.

macOS has no dedicated server. `Detect` refuses a macOS game client (a `Valheim.app` bundle, a renamed bundle recognised by `Contents/MacOS/Valheim`, or a directory holding `Valheim.app`) when no server executable is present, and on a macOS host `RequireExecutable` and `CreateStartInfo` refuse either server before reading its mode. Both throw `PlatformNotSupportedException` naming the Linux container (`--platform linux/amd64`, experimental on Apple Silicon) or a remote Windows/Linux host. Detection itself still works on a Mac. Windows and Linux hosts are unchanged. The host is injectable internally, so every host's branches are unit-tested on any OS, and CI runs the tests on Windows, Linux and macOS.

The image pins `linux/amd64` and was verified on a Linux x86-64 Docker host on 28 September 2026 (server build 25527701): the smoke passed all five steps and `validate.cs` passed inside the container.

Limits: this boots a server with no ValheimCLI or mod plugins; it does not test save, networking or gameplay, and Steam's backend connection is recorded but not required. Unit tests cover detection, refusal and environment merging with fake files. Game clients in the cloud are not supported, and remote hosts over SSH are the next step. Images contain game files and must never be pushed or published.

## Game client launch (preview 11)

`ClientLaunch` is the client twin of `ServerLaunch`: it builds the `ProcessStartInfo` for one BepInEx game client from its install directory and starts nothing. The platform comes from the install's contents, never the host: `valheim.exe` is Windows, `valheim.x86_64` is Linux and a `Valheim.app` bundle (any case) is macOS. An install with more than one is refused, as is the bundle itself (pass the directory that holds it and BepInEx) and a dedicated-server runtime, which is pointed at `ServerLaunch`; `ServerLaunch` likewise names `ClientLaunch` when given a client. A client runs only on its own OS, so any other host throws `PlatformNotSupportedException`; there is no Wine or Proton support. `Detect` still works on any host.

| | Windows | Linux | macOS |
|---|---|---|---|
| Detected from | `valheim.exe` | `valheim.x86_64` (user-execute bit required) | `Valheim.app/Contents/MacOS/Valheim` (user-execute bit required) |
| Loader files required | `BepInEx/core/BepInEx.Preloader.dll` and `BepInEx.dll`; `winhttp.dll`; `doorstop_config.ini` enabling Doorstop with an existing target, in Doorstop 4 (`[General] enabled`, `target_assembly`) or Doorstop 3 (`[UnityDoorstop] enabled`, `targetAssembly`) form | BepInEx core; `doorstop_libs/libdoorstop_x64.so` | BepInEx core; `doorstop_libs/libdoorstop_x64.dylib` (BepInExPack_Valheim) or a universal `libdoorstop.dylib` (BepInEx's macOS build) with the requested slice |
| Started executable | `valheim.exe` | `valheim.x86_64` | `/usr/bin/arch -x86_64` or `-arm64`, then the bundle executable |
| Loader variables set | none; the proxy DLL reads its ini | `DOORSTOP_ENABLED=1`, `DOORSTOP_TARGET_ASSEMBLY`, `doorstop_libs` prepended to `LD_LIBRARY_PATH`, `libdoorstop_x64.so` prepended to `LD_PRELOAD` | `DOORSTOP_ENABLED=1`, `DOORSTOP_TARGET_ASSEMBLY`, the library's full path prepended to `DYLD_INSERT_LIBRARIES` and passed with `arch -e` |
| Architectures | x64 | x64 | x86_64 (default; Rosetta on Apple Silicon) or native arm64 |
| Also needed to run | desktop session, Steam | desktop session with `DISPLAY` or `WAYLAND_DISPLAY`, GPU, Steam | logged-in desktop session, Steam |

On every platform `-console` is added first unless the caller passes `console: false` or already includes it, other arguments follow unchanged, `SteamAppId` defaults to 892970 unless the caller sets it, and caller environment is applied first with existing search-list entries kept. A caller value for `DOORSTOP_ENABLED`, `DOORSTOP_TARGET_ASSEMBLY` or `DOORSTOP_DISABLE`, or a `--doorstop-*` argument, is refused because it would start a vanilla client; the same variables are removed if inherited. Linux and macOS install paths may not contain `:` (Linux also `;`), which their search lists cannot represent.

On macOS the game is started through `/usr/bin/arch` with an explicit slice, because a universal executable otherwise runs as the parent's architecture, which the Doorstop library may lack. `arch` is a protected system binary, so the kernel drops `DYLD_*` from its environment; `ClientLaunch` passes them with `-e` instead, and `arch` execs the game in place, so the started PID is the game's. It reads the Mach-O slices of the game and of each Doorstop library and inserts the first library with the requested slice, refusing with the slices it found otherwise. BepInExPack_Valheim ships only an x86_64 library, which is its supported route. A native arm64 launch needs the universal `libdoorstop.dylib` and a BepInEx core that runs natively on arm64, which this check cannot see. `LaunchArchitectures` reports the slices an install can be launched as.

Limits: `ClientLaunch` only builds the launch. A game client needs an interactive desktop session with a display, a GPU and a running, signed-in Steam client. A process started from a service, a scheduled task without a logged-in user or an SSH session usually has no display, so it fails or never shows a window. Starting the plan inside the user's session, and anything remote, is left to a later host adapter. Unit tests cover detection, refusals, loader checks, slice selection and environment merging with fake install trees on all three platforms; no client has been started through `ClientLaunch` yet.
