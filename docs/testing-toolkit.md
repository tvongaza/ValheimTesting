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
| `Valheim.Testing.Game` | net10.0, named actors, typed observations, event waits (log line, process exit, game state) with bounded fallbacks, fixture copies, comparisons and JSON/JUnit reports, game hosts (local, SSH, container) and environment profiles |
| `Valheim.Testing.Doubles` | source package, compiled into the consumer: partial doubles of the Unity, Valheim, BepInEx and Jotunn types mod logic uses (see [Game doubles](#game-doubles)) |
| `Valheim.Testing.Bindings` | netstandard2.0, offline check that a built mod's references into the game assemblies still bind, with Mono.Cecil (see [Offline binding check](#offline-binding-check-valheimtestingbindings-preview-1)) |
| `Valheim.Testing.Bindings.Tool` | net10.0 .NET tool `valheim-bindings`: the same check as a CI step |
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

A `GameActor` wraps the existing transport; it never owns/stops the attached process. Verify `cli_expect` pins before using an actor (the only alternative is the explicit opt-out in [Pins and the opt-out](#pins-and-the-opt-out)). Require a capability/version, issue a mutation once, then poll its read-only observation. Unknown outcomes are failures, not permission to resend. Keep the command timeout below the overall scenario allowance: a synchronous read cannot be forcibly interrupted by an observation deadline.

`WorldFixture.Copy` requires a hash manifest, refuses links and unexpected files, verifies copied bytes and creates its own new directory (`CopyAsFound` records the hashes instead, for an explicitly unpinned run only). It deletes only that copy; `Preserve=true` keeps it for debugging. Stop the owned game BEFORE disposing its world fixture. It does not own accounts, characters, ports or remote machine reservations. Those remain explicit session-operator responsibilities in preview 1.

`ScenarioReport` records outcomes and teardown failure in JSON/JUnit. Use a unique output directory per run; include fixture/build/config hashes in `Provenance`. Output may contain player/world data; inspect before publishing. Do not include credentials in fixture configuration or logs.

## Waiting

Wait for the event that announces a change, not for time to pass. Every wait takes an explicit, finite timeout; there is no default. Expiry throws `WaitTimeoutException` (a `TimeoutException`) and an early failure throws `WaitFailedException` (an `InvalidOperationException`). Both report what was awaited, the elapsed time and the last thing seen.

| Source | Wait | Notes |
|---|---|---|
| A log line | `LogWait` | Opens at the file's current end (or a given byte offset), so earlier lines never match; the file may not exist yet. Matches complete lines only, checks optional failure patterns first, and re-reads a truncated or replaced file from its start. A `FileSystemWatcher` wakes the wait; a 2 s re-read covers filesystems that drop watcher events (network shares, container mounts). |
| A process exit | `ProcessWait.ForExitAsync` | Returns the exit code; expiry leaves the process running for its owner to stop. |
| A game state | `StateWait` | Subscribes to ValheimCLI's state pushes on a connection used for nothing else, asks the current state once, then awaits `STATE_CHANGED` pushes with a pending read; nothing is sent while it waits. A push that arrives during the question counts. A dedicated server's loaded world is `InWorldNoPlayer`. A state is not mod readiness. The transport keeps no history: a wait sees the current state and pushes from its own subscription on, not pushes sent before it connected. |
| No event | `Check.Eventually` | Bounded fallback: re-observes a read-only source at an interval, for example an adapter's `complete` flag. |

```csharp
using var log = new LogWait(Path.Combine(runtime, "BepInEx", "LogOutput.log")); // before launching
// ... start the server ...
await log.WaitAsync(StartupEvents.CliListening, TimeSpan.FromMinutes(5), [LogWait.Literal("[Fatal")]);
```

`OwnedServerSession` waits on these when given `Events = new StartupEvents { CliLog = ..., Failures = StartupEvents.StartupFailures, States = () => StateWait.Connect(host, port) }`: no connection before this boot's `Command server listening` line, then the world-loaded push, then the session probe. The process exit is always watched: a server that exits during startup fails it at once with its exit code and the last log line. When that boot wrote nothing to its BepInEx log, the failure says so and points at Unity's player log (`Player.log` or the `-logFile` file) and at security software, a known cause of a game stopped before BepInEx starts. `StartupFailures` ends startup at once on:
- BepInEx's own "Could not load [" and "Error loading [" lines (`BepInExPluginLoadFailures`), so a pinned runtime with a missing dependency fails at once instead of at the readiness deadline;
- a `TypeLoadException`, `MissingMethodException` or `MissingFieldException` in a BepInEx warning, error or fatal line, or in an exception line that starts with its name (`System.TypeLoadException: Could not load type of field 'ZDO:...'`, as a leftover preloader patcher causes), and ValheimCLI's packs logging `CLI core 1.1 is not ready.` (`RuntimeLoadFailures`). An info or debug line that only mentions one of these exceptions, such as a mod's handled soft dependency, does not end startup.

A log failure carries the matched line and the five lines read before it (`WaitFailedException.Context`, also in its message). `DedicatedStartupEvents` and owned `ClientSession` launches use `StartupFailures`. Only the adapter's own readiness, which has no event, is re-probed at the poll interval. The startup deadline bounds every step, including a connection or command that blocks: each races the process exit and the time left, a connection that completes after startup gave up is closed, and the final pin verification gets only the time left (at most the command timeout). Without `Events`, connecting keeps its bounded retries. `GameActor.WaitForEnvironment` takes an event too, for example `(left, token) => log.WaitAsync(loadLine, left, cancellation: token)`: it rechecks the pins when the reload's line appears, not every 200 ms.

`StateWait` closes its connection when a wait expires or is cancelled, because the abandoned read leaves the stream's position unknown; later waits on that instance fail, so connect a new one. A failure state, a closed connection or a line that is not a push fails the wait at once.

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

The package examples accept `-p:ToolkitPackageVersion=0.1.0-preview.6` instead of
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

`PlayerPlacement` (preview 11; intro skipping preview 12; protection by default and fly preview 13) packages that procedure for any mod's client check:

- `SkipIntro(client, timeout)` ends a new character's first-spawn intro (the Valkyrie ride) as the menu's Skip does, or stops it before it starts, and waits for the player to respawn on the ground (`cli_skip_intro`, not a cheat command). It returns whether an intro was running; for a character that has spawned before it changes nothing.
- `Protect(client)` turns on god, ghost and debug modes (`cli_set_player_safety true`, which sets them rather than toggling) and requires the game to read all three back; it is issued once, and an unconfirmed reply fails. Debug flying stays off. `SessionControl.WaitForWorld` calls it by default (see below), so a joined player is protected without asking.
- `SetFly(client, on)` turns debug fly on or off (`cli_fly on|off`) and requires the game to read the state back. Fly is for review and visual steps only: a flying player is never supported, so `Arrive` and `RequireSupported` refuse (an `InvalidOperationException`, not a `SupportException`) as soon as a reading shows the player flying. Turn it off before measuring again.
- `Arrive(server, client, point, timeout)` first skips the intro (pass `skipIntro: false` to leave it), because the game silently drops a teleport during the Valkyrie ride and within 2 s of a spawn; it then waits for the player to hold still for 3 s (standing or swimming: a character that logged out where this world copy has water spawns swimming, and the game teleports it like a standing one), finds the server's only connected player with a character (none or several is refused), asks the server once to teleport it just above the point with the game's own teleport, and waits until the client's own observations show the player settled there. A teleport reply is never taken as arrival, and a timeout does not repeat the teleport.
- `RequireSupported(client, point)` takes three readings half a second apart, each of which must show the player grounded and stationary on the declared ground; a `SupportException` carries every reading.

`SessionControl.Join` (preview 12) turns the client's devcommands on before joining, because ValheimCLI refuses the join (a mutating extension command) until it is on; `EnableDevcommands()` does the same on its own. Both read the game's reply and toggle again if that turned it off.

`SessionControl.WaitForWorld` (preview 13) protects the local player once the world is ready (`PlayerPlacement.Protect`), so a test that forgets to cannot lose its character to a fall, the water or a mob before it arrives, a failure that would look like a mod bug. A protection the game does not read back fails the wait. A dedicated server has no local player and is left alone; a game hosting its own world reports ready before its player spawns, so the wait goes on, within the same timeout, until that player exists, and times out saying so if it never does. On a joined client the command needs ValheimCLI's `AllowOnServerClients` setting, like the join.

Protection changes gameplay (as the game does in 1.0.16): monsters neither notice nor target a player in ghost mode, an egg hatches only near a player who is not in ghost mode, and a creature hit by a player in god or ghost mode is marked cheated, which marks the items it drops. Pass `WaitForWorld(..., protectPlayer: false)` for combat, aggro, taming, hatching and loot checks, for any check that needs a vulnerable player, and when an operator manages these modes; call `PlayerPlacement.Protect` yourself for the steps that should be protected.

Declare the support point on dry ground: a player standing in water is not grounded at the ground's height, so the check fails, which is correct but tells you nothing about the road. `examples/ClientSurfaceCheck` uses `RequireSupported`.


## Published-library validation update

Roads exercises real production sources against the shared terrain model and source doubles, with mod-specific extensions and scenario assertions kept in Roads. The [consumer quickstart](../examples/ModWithTests/README.md) shows that arrangement on a smaller mod. Current suite counts belong in the relevant CI/run report; normal mod builds do not depend on the test libraries.

The native paint extension has now been exercised on Valheim 1.0.16: sixteen saved RGBA texels across two zones, paved core plus untouched painted verge, alpha preserved, before and after save/server restart/rejoin on a ValheimCLI-only client. The same plan failed exactly eight samples when deliberately given the unchanged pre-road expectation. Height, collider and stationary support checks passed alongside paint. The [follow-up campaign](native-validation-20260927.md) adds native dirt/fading-edge coverage. Rendered appearance and human walking remain follow-ups.

The four-pack layout also passed join and confirmed-save paths. Optional Reflection was removed and reloaded in a loaded dedicated world over the same connection; only its owner identity changed. This establishes lifecycle behavior, not reclamation of loaded assemblies. Native checks exposed a missing Mono verification build setting in the packs, which is corrected in ValheimCLI #40.

## Pinned server runner

`PinnedServerRun.MainAsync` is the lifecycle of a mod's owned dedicated-server test runner, so the mod's `Program.cs` supplies only its plan fields, modes and scenarios. Usage is `<runner> validate|run|<prepare modes> <plan.json> <new-output-directory>`. A plan derives from `ServerRunPlan`, which is read strictly (unknown fields refused), and `ValidateServerPlan` applies the rules every pinned plan follows:
- pinned sources, and port and time bounds;
- a known executable;
- no runner-owned token or Doorstop variable in the environment;
- `-batchmode -nographics` and exactly one `-savedir {world}`;
- strict pins, with `worlduid` and an exact MD5 for every plugin;
- `runtimePins`: the runtime's game build, BepInEx core and patchers (see [Pins and the opt-out](#pins-and-the-opt-out)).

The plan may also list `patchers` (every entry in the runtime's `BepInEx/patchers`, by name) and `logScan` (per-run severities for the log scan below, each with a reason).

The plan may also list `patchers` (every entry in the runtime's `BepInEx/patchers`, by name) and `logScan` (per-run severities for the log scan below, each with a reason).

The runner then:
1. refuses an existing output directory, and checks the host before copying;
2. copies and verifies the runtime and world, recording plan, runner and toolkit hashes, mode, platform and input hashes, refuses a runtime whose `BepInEx/patchers` holds anything the plan's `patchers` does not name (or lacks a named one), and refuses a copy whose game build, BepInEx core or patchers differ from `runtimePins` (the values found are recorded as `runtimeGameSha256`, `runtimeBepInExCoreSha256` and `runtimePatchersSha256`);
3. stops there for `validate`;
4. otherwise checks the CLI port and starts the owned session on the copies, with per-boot logs, recorded commands and `DedicatedStartupEvents`, then enables devcommands through the session capability and runs the scenario;
5. always stops only the owned server, scans every boot's logs and the scenario's `run.Logs` (a `scan run logs` step, see below), writes `result.json` and `junit.xml`, and prints PASS (only for `run`), VALIDATED or PREPARED, or FAIL.

A clean test runtime is BepInEx core plus your plugins, with an empty `patchers` folder. Preloader patchers rewrite game assemblies before any plugin loads, so one a removed mod left behind breaks the whole run with a `TypeLoadException` on a game type; name a patcher in the plan only when the run needs it (for example a hook generator a dependency requires).

### Pins and the opt-out

Strict pins are the default everywhere and the recommended mode. Each pin protects against a run that passes on something other than what you meant to test:

| Pin | Where | Protects against |
|---|---|---|
| `worlduid` | plan `pins`, checked in game by `cli_expect --strict` before every command | another world, or a world transition the test did not expect |
| plugin MD5s, `absent` | plan and client `pins`, same check | a stale or different mod build, an extra plugin, a mod that should not be there |
| runtime and world SHA256 manifests | plan `runtime` and `world` | a changed fixture: any file in the runtime or save |
| `game` | `runtimePins`, client `installPins` | another game build that changes the game's code: a hotfix, or the `default_old` / `default_pre1_0` branches |
| `bepinexCore` | same | another BepInEx or BepInExPack build (Valheim 1.0 shipped a new pack) |
| `patchers` | same, beside the `patchers` names | another build of a named preloader patcher |

ValheimCLI's `cli_expect` knows plugins and the world only, so the game build and the loader are pinned on disk before launch (`InstallPins`). Each value is the SHA256 of a listing: one line per file, `<sha256>  <relative path>` with `/` separators, ordered by path, as `sha256sum` prints it; Finder's `.DS_Store` and AppleDouble `._*` files (which a copy through macOS adds) are left out, and an empty or absent folder hashes the empty listing. `game` lists every `assembly_*.dll` in the game's Managed folder (`<executable>_Data/Managed`, or `Valheim.app/Contents/Resources/Data/Managed` on macOS): `assembly_valheim`, `assembly_utils`, `assembly_guiutils`, `assembly_postprocessing` and the rest of the game's own code, which exists in a copied runtime and in a client install and needs no Steam files. A build that changes only assets or Unity's own assemblies keeps the same `game` value. Steam's build id is not in the install (it is in Steam's `appmanifest_*.acf`, outside the game folder and absent from a runtime copy), `steam_appid.txt` holds only the app id, and the executable's version is Unity's. `bepinexCore` and `patchers` list the whole folder, subfolders included. `InstallPins.Of(path)` computes all three; a mismatch names what differs and the value found. Without the library, in the folder concerned: `find . -type f ! -name .DS_Store ! -name '._*' | sed 's|^\./||' | LC_ALL=C sort | while IFS= read -r f; do printf '%s  %s\n' "$(sha256sum "$f" | cut -d' ' -f1)" "$f"; done | sha256sum` (for `game`, list `ls assembly_*.dll` in the Managed folder instead of `find`). The runtime manifest also covers these files, but a manifest regenerated after an update silently accepts the new build; `runtimePins` makes that a deliberate change.

**Upgrading plans (breaking since this preview).** Every strict plan now needs `runtimePins`, and an owned client section needs `installPins`; `ValidateServerPlan` and `ClientRunPlan.Validate` refuse a plan without them. This includes every mod runner whose plan derives from `ServerRunPlan` and calls `ValidateServerPlan`, such as ProceduralRoads' system-test plans and the FullLifecycle example. Compute the values once with `InstallPins.Of(<runtime>)` and `InstallPins.Of(<client install>)` (or the shell listing above), review them, and add them to the plan: `"runtimePins": { "game": "...", "bepinexCore": "...", "patchers": "..." }`, and the same object as `"installPins"` in the client section. An attached client needs neither.

An owned client pins its install with `installPins`; the launch refuses a mismatch before anything starts. An attached client's install is its operator's and is not read, so its game build is not pinned: the game refuses a join only across network versions, which builds of one release usually share. Use an owned client when the client's build matters.

`"pinning": "none"` in a plan (or a client section) is the explicit opt-out, for trying the toolkit before keeping pins. It is never a default: an omitted `pinning` is `strict`, any other value is refused, and no environment variable sets it. An unpinned plan lists no `pins` or `runtimePins` (so nothing looks enforced that is not) and may leave out the fixture manifests, whose copies are then recorded as found (`WorldFixture.CopyAsFound`). The runner prints `WARNING: environment not pinned: ...` once the plan is read, records the runtime's `InstallPins` without checking them, and marks every report and evidence file: `result.json` (`Pinning: "none"` and provenance `environment`), `junit.xml` (an `environment` property and a skipped test case named `environment not pinned`), `input-hashes.json`, `boot-N.process.json`, the first line of `connection-N.jsonl`, and the result banner. The run can still pass. An unpinned client's actor runs without `cli_expect`, prints the warning, and marks `client-commands.jsonl` and `client-process.json`. For your own actors, `GameActor.VerifyEnvironment(EnvironmentPinning.None)` is the same opt-out (`Pinned` is then false until pins are verified again). A result without pins is not evidence for any particular build; pin it before you rely on it.

### Log scan at teardown

Many mod failures only log a warning, or appear long after startup. After the processes stop, `ScenarioReport.ScanLogs(logs, classifications)` reads each `RunLog` (a role, the kept copy's path and whether it is required) with `LogScanner` and records the `scan run logs` step and one `Logs` entry per file in `result.json`: every pattern's severity, count, first line number and first line. The step fails when a failure pattern matched or a required log is missing; warnings are only counted. `PinnedServerRun` scans, per boot, the BepInEx log (required), the Unity log (the copy of `{runtime}/toolkit-unity.log`, so pass `-logFile {runtime}/toolkit-unity.log` in the plan's arguments) and the process output (Unity's log on Linux without `-logFile`). An owned client's `ClientSession.Logs` (its BepInEx log and `Player.log`) are added by the scenario to `run.Logs`, as the [FullLifecycle example](../examples/FullLifecycle/MyMod.SystemTests/Program.cs) does.

| Pattern | Matches | Default |
|---|---|---|
| `harmony-unpatch-all` | HarmonyX "UnpatchAll has been called - This will remove ALL HARMONY PATCHES": a mod removed every mod's patches (not the skipped "Legacy UnpatchAll has been called AND DisallowLegacyGlobalUnpatchAll=true") | failure |
| `harmony-undefined-target` | HarmonyX "Undefined target method for patch method": the patched method does not exist | failure |
| `accesstools-not-found` | HarmonyX `AccessTools.*: Could not find ...` (mods also probe optional members this way) | warning |
| `missing-method`, `missing-field`, `type-load` | `MissingMethodException`, `MissingFieldException`, `TypeLoadException` | failure |
| `nre-remove-objects` | a `NullReferenceException` whose stack trace (up to a blank line or the next BepInEx header) has `ZNetScene.RemoveObjects` | failure |
| `rpc-method-missing` | the game's "Failed to find rpc method" for a per-object RPC without a handler | warning |
| `missing-script` | Unity's own "The referenced script ... is missing" warning (asset bundle scripts not loaded) | warning |
| `shader-unsupported` | Unity's "Desired shader compiler platform N is not available in shader blob", "not supported on this GPU" and "Shader Unsupported" (magenta bundles on Vulkan or OpenGL clients; wording as in the Valheim-Modding wiki's [Valheim-Unity-Project-Guide](https://github.com/Valheim-Modding/Wiki/wiki/Valheim-Unity-Project-Guide)) | warning |
| `unknown-warning`, `unknown-error` | BepInEx warning, or error and fatal, lines (with their continuation lines) that match no pattern above | warning |

Owned processes are killed at teardown (`Process.Kill`), not asked to quit, so the scan sees only what was logged during the run. Anything logged while the game shuts down, such as a mod calling UnpatchAll when the game closes (the usual case for that warning), is never written and cannot be counted.

A plan's `"logScan": { "rpc-method-missing": { "severity": "Failure", "reason": "..." } }` changes a severity for that run; the reason is required and recorded beside the count. An absent log has no counts (absent is not zero). A missing required BepInEx log fails the scan: when the game never created it, the message points at the Unity log and at security software; otherwise its process was not stopped by its session, which copies the logs when it stops. Counts are per file, and the same Unity message may appear in both the BepInEx log and `Player.log`. `Player.log` has no levels, so only the named patterns count there. Which lines a clean vanilla or headless run logs, and so which counts to expect, has not been measured on a native run yet.

Ctrl+C and SIGTERM cancel the run. Options name the session capability and token variable, gate modes against the plan (`CheckMode`) and add provenance.

## Test fakes

`Valheim.Testing.Game.Fakes` tests scenario and session code without a game:
- `ScriptedTransport` answers strict pins, the extension listing, registered extension commands (in the real `EXTENSION_RESULT` envelope), confirmed saves and any command a test registers. It throws on anything unscripted, records every command, and `Actor()` returns a pinned `GameActor` on it.
- `FakeOwnedServer` launches `FakeServerProcess`es and answers the session probe and pins as an owned server with a session adapter. Switches make each failure happen: not ready, wrong PID, refused pins, exit on launch, refused connections, refused stop. Gates hold a connection or pin check open. `ConnectEntered`/`PinEntered` let a test inject a failure at exactly that stage, and `Events` records the lifecycle.
- `TempRuntime` is a runtime directory whose BepInEx log a test appends to or rewrites, as each boot does.

## Game doubles

For a complete package-consuming project that links real mod source and runs five tests, start with [ModWithTests](../examples/ModWithTests/README.md). The reference below describes the supported model and its limits.

`Valheim.Testing.Doubles` lets a unit-test project compile a mod's pure-logic source files (linked with `<Compile Include="../YourMod/Src/....cs" />`) without Unity, Valheim or BepInEx. It is a source package: its files compile into your test project and stand in for the game's types under their real names (`UnityEngine.Vector3`, `UnityEngine.Object` and `GameObject`, the global `ZDO`, `ZDOMan`, `ZNetView`, `ZNetScene`, `Heightmap`, `TerrainComp`, `ZoneSystem`, `WorldGenerator`, `ZNet`, `ZNetPeer`, `ZRoutedRpc`, `ZPackage`, `ISerializableParameter`, `HitData`, `ZLog`, `Terminal` and its console commands, `Player.m_localPlayer`, `BepInEx.Logging.ManualLogSource`, Jotunn's `CustomRPC`). So the test project must not also reference the game's assemblies. It needs C# 10 and works on net48 and modern .NET. Reference it with `PrivateAssets="all"` (it is a development dependency).

Every type is `partial`: add the members your mod calls in your own files. Keep only mod-specific behaviour there. Two `Heightmap` hooks carry a mod's terrain logic into the rebuild: `ModBaseHeight` (for example a biome blend) and `ModTerrainPass` (the seam a Harmony prefix on the game's rebuild uses).

The doubles copy the game where mod code depends on it, and their own tests check these points:
- ZDO values are keyed by `GetStableHashCode` of their name, as in the game, so `Set("name", v)` and `Set("name".GetStableHashCode(), v)` are the same key for every value type (float, `Vector3`, `Quaternion`, int, bool, long, string, byte array). Each type has its own table; a bool is an int. The hash matches the game's for every string.
- `ZDOMan.DestroyZDO` is queued, so a destroyed ZDO stays visible to `FindObjects` until `ProcessDestroyed`.
- A destroyed `UnityEngine.Object` compares equal to null, as in Unity (see [Destroyed objects](#destroyed-objects)).
- A networked prefab instantiated during ghost initialisation leaves its ZDO and joins no live scene.
- `ZNetScene.Destroy` resets the view's ZDO (`GetZDO()` is null afterwards), removes the object from the live scene at once and queues only ZDOs this session owns; a compiler saves only when owned.
- Rebuilt heights are clamped to the game's ±8 m.
- Heightmaps are loaded per zone and found by position: `Heightmap.FindHeightmap(point)` returns the loaded zone that holds the point (edges included) or null, `GetAllHeightmaps()` lists every loaded zone, and `TerrainComp.FindTerrainCompiler(pos)` returns that zone's compiler. A write aimed at the wrong zone, or at a zone that is not loaded, therefore misses as it would in the game.
- Zone ids narrow to `short`.
- Valheim 1.0's locations-generated flag is set from a save without raising the event.
- `ZPackage` has every Write/Read pair of the game (1.0.16) with its byte encoding: little-endian numbers, strings with a 7-bit length prefix and UTF-8, a byte array or nested package with an int length, a `ZDOID` as the creator's session id then a uint, vectors, quaternions, small rotations and item counts. It is one stream written and read at the same position, so write, then `SetPos(0)` to read back. `GetArray`, `GetPos`, `Size`, `Clear`, `Load` and the `byte[]` and base64 constructors are there too. Compressed packages round-trip, but their bytes differ from the game's: the runtime's gzip at its default level, not the game's at its Fastest level.
- Routed RPCs (`ZRoutedRpc`) follow the game's rules:
  - Arguments are written into a `ZPackage` with the game's type table (int, uint, long, float, double, bool, string, `ZPackage`, `List<string>`, `Vector3`, `Quaternion`, `ZDOID`, `HitData`, `ISerializableParameter`), and the handler reads them back by its own parameter types, so it gets copies. The game skips any other argument without an error (an enum, a byte, a `byte[]`, a `Vector2i`) and its handler then reads the wrong bytes; the double throws an `ArgumentException` naming the argument. Where the game would misread (an int sent to a long handler, fewer arguments than parameters) or cannot pass a value (a parameter of an unreadable type), the double throws an `InvalidOperationException` naming the parameter, so the test fails at the cause rather than on a wrong value later. The type check is exact, so it also refuses reinterpretations the game gets away with (an int read as a uint, a subclass sent to a base-class parameter): refused, not misread. Extra trailing arguments are ignored, as in the game. `HitData` has the game's fields and defaults and writes itself with the game's layout (a flags word, then only the values that differ from their defaults), so a hit sent through a routed RPC arrives as in the game; its damage calculation is not doubled, and `Skills` has only its `SkillType` ids.
  - A call to `Everybody` or to this peer runs the local handler at once, before anything is sent. A "client-only" broadcast handler therefore also runs on the server, and on a listen-server host (which has a local player) its client branch runs too.
  - A call to a name nobody registered is dropped without an error. `ZRoutedRpc.Dropped` records it, so a test can assert that nothing was dropped. Registering a name twice throws, as in the game. Handlers take up to six arguments after the sender. `ZRoutedRpc.IsRegistered(name)` and `ZNetView.IsRegistered(name)` are not game methods: use them in tests only, never in mod code.
  - Upgrading from preview 4: a second registration used to replace the first silently. A suite that runs a mod's registration more than once on the default static `ZRoutedRpc.instance` should install a fresh one per test with `ValheimWorldScope.WithNetwork()`.
  - Without a target, the server calls itself, a client calls its first ready peer, or `Everybody` when it has none. `GetServerPeerID()` returns that target; it is private in the game and stays public here for mods built against publicized assemblies.
  - This peer's id (`PeerId`) is the session id (`ZDOMan.m_sessionID`, 1 by default) unless `SetUID` sets another. The server sends to the target peer, or a broadcast to every ready peer except its sender; a client sends to its ready peers. `ZRoutedRpc.Sent` holds each peer's message as that peer decodes it. A peer with `Ready = false` is still joining: its `m_uid` is 0 and it receives nothing. `Invoked` records every call as made. `Deliver(sender, method, args)` handles a call arriving at this peer, and `Receive(sender, target, zdo, method, args)` handles any arriving call, including one the server relays.
- Per-object RPCs: `ZNetView.Register` (up to six arguments), `Unregister` and `InvokeRPC` work as in the game, through the routed RPCs with the object's ZDO id. `InvokeRPC(method, ...)` goes to the object's owner (an object without an owner broadcasts), `InvokeRPC(ZNetView.Everybody, ...)` to everyone. A call reaches the view whose ZDO is in `ZDOMan.instance` and, when a scene is installed, whose object is live in it; a view built around a bare ZDO, with no game object, is found through its ZDO. A name the view did not register logs `Failed to find rpc method <hash>` through `ZLog` (into the log capture), as the game does; a call to a missing object is dropped silently. Both land in `ZRoutedRpc.Dropped`.
- Jotunn RPCs are kept by name in `NetworkManager.Instance.Rpcs` and record what they send.
- Constructing a `Terminal.ConsoleCommand` registers it under its lower-case name. `Terminal.TryRunCommand` runs it with that terminal as `args.Context` and prints an unknown command or a failed failable action; `Terminal.Output` holds what was printed. Cheat, server-only and admin gating is not modelled; the mod's own checks still run.

A save and restart is `ZDOMan.instance.RoundTripThroughSave()` (the world) or `zdo.RoundTripThroughSave()` (one persistent ZDO), modelled on Valheim 1.0.16:
- Only persistent ZDOs are saved. Each comes back with no owner and a new id, so a stored `ZDOID` no longer finds it.
- Values under a session-only hash are not saved. That is the game's list (physics and AI state such as `support`, `vel`, `InUse`: a `WearNTear` support value is recomputed after a restart) plus every hash registered with `zdo.AddSessionHash(hash)`, which a reload forgets. `ZDOMan.SessionOnlyHashes` holds the current set.
- Loading a 1.0 save strips nothing else: an empty string, an empty byte array or an identity rotation survives. Older descriptions of a load-time strip describe the upgrade of an old world. `RoundTripThroughSave(ZDO.SavedWorld.BeforeChunkedSave)` models the first 1.0.16 load of a world an older game saved (the ZDO's values stand for that save): it removes the game's named legacy keys (for example `support`, `burnt0`..`burnt10`, `room<n>_seed`, `<n>_crafterName`). `SavedWorld.BeforeNewSaveFormat` also drops empty strings, empty byte arrays and identity rotations, and renames the old spawn keys. The upgrade's other conversions and its unnamed legacy hashes are not modelled.
- The game's animator sync stores animator parameters in the ZDO under `Animator.StringToHash(name) + 438569` (ints for bools and ints, floats for floats) and registers each as session-only, so they never reach the save. The doubles have no `Animator`; a mod that reads animation state from a ZDO passes the hash it computed.

`TerrainSnapshot.Of(compiler)` copies a zone compiler's level and smooth deltas, paint mask and both modified flags; `TerrainAssert` compares the compiler with it afterwards:
- `OnlyChangedWithin(before, compiler, (x, z) => ...)`: every change lies inside the footprint you declare, and the rest of the zone, flags included, is exactly as it was. It also fails when nothing changed, since then it proves nothing.
- `Unchanged(before, compiler)`: nothing changed at all.
- `SeamAgrees(west, east)`: two loaded neighbours agree on their shared vertices, rendered height and paint (rebuild both first). A zone written without its neighbour shows up here.

### Destroyed objects

`UnityEngine.Object` has Unity's overloaded `==`, `!=`, `Equals` and `bool` conversion: a destroyed object is equal to null and `if (obj)` is false. `is null`, `?.` and `??` skip the overload and see a live reference, as in Unity. Engine members of a destroyed object (`name`, `gameObject`, `GetComponent`) throw a `NullReferenceException`, as the game's player does (the log under [Do NOT use null operators on GameObject](https://github.com/Valheim-Modding/Wiki/wiki/Best-Practices#do-not-use-null-operators-on-gameobject) on the modding wiki shows `GameObject.GetComponent` throwing it). The double's message names the destroyed object; `UnityEngine.MissingReferenceException` exists only so code that names it compiles. A component's own fields and methods (`WearNTear.m_health`, `ZNetView.GetZDO()`) still answer, as they do in Unity. Live objects and real null references compare as before, and objects stay distinct dictionary keys and list entries after they are destroyed.

`Object.Destroy` is deferred like Unity's end-of-frame destruction: the object stays alive until the test calls `UnityEngine.Object.EndOfFrame()` (it returns how many objects went). `DestroyImmediate` destroys at once. Destroying a `GameObject` destroys its `ZNetView` and `WearNTear`; destroying only a component makes `GetComponent` return null for it. A plain `Object.Destroy` does not touch the live scene, as in the game, where only `ZNetScene` removes its instances (the doubles model its `Destroy`): after a plain `Object.Destroy` the destroyed view stays in `ZNetScene.Live` and `FindInstance` still returns it (it equals null), the invalid object the wiki's [ZNetScene.RemoveObjects](https://github.com/Valheim-Modding/Wiki/wiki/Client-Troubleshooting#znetsceneremoveobjects) error comes from. Pending destroys are process-wide: `ValheimWorldScope` ends the frame when disposed; without a scope, call `EndOfFrame()` in the test or its `Dispose` so none carry into the next test.

This catches the `?.` mistake the modding wiki warns about. Say a mod's `PieceHealth.Total(pieces)` adds up `piece?.GetComponent<WearNTear>()?.m_health ?? 0f`. This test then fails with a `NullReferenceException`; once the mod checks `if (piece == null) continue;` before `GetComponent`, it passes:

```csharp
[Fact] public void DestroyedPiecesAreSkipped()
{
    using var world = new ValheimWorldScope().WithZdos().WithScene();
    var prefab = ZNetScene.instance!.AddPrefab("stone_wall", health: 1500f);
    var kept = UnityEngine.Object.Instantiate(prefab, new UnityEngine.Vector3(1, 30, 1), UnityEngine.Quaternion.Euler(0, 0, 0));
    var removed = UnityEngine.Object.Instantiate(prefab, new UnityEngine.Vector3(2, 30, 2), UnityEngine.Quaternion.Euler(0, 0, 0));
    UnityEngine.Object.Destroy(removed);
    UnityEngine.Object.EndOfFrame();
    Assert.Equal(1500f, PieceHealth.Total(new[] { kept, removed }));
}
```

`WorldGenerator` is virtual, so tests plug in synthetic worlds; `TerrainWorld` puts any `Valheim.Testing` terrain behind it.

Setup, in the unit-test project (not the mod project):

```xml
<PropertyGroup><LangVersion>10</LangVersion></PropertyGroup> <!-- or newer; net48 test legs default to C# 7.3 -->
<ItemGroup>
  <PackageReference Include="Valheim.Testing.Doubles" Version="[0.1.0-preview.4]" PrivateAssets="all" />
  <Compile Include="../MyMod/Src/RoadMath.cs" /> <!-- the mod's pure-logic sources -->
</ItemGroup>
```

The doubles are process-wide statics, as the game's singletons are, so add `[assembly: CollectionBehavior(DisableTestParallelization = true)]` once in the test project.

`ValheimWorldScope` gives each test its own world. It records which objects `WorldGenerator.instance`, `ZDOMan.instance`, `ZoneSystem.instance`, `ZNetScene.instance`, `ZNet.instance`, `ZRoutedRpc.instance`, Jotunn's `NetworkManager.Instance`, the loaded heightmaps, the log capture, `Terminal.commands` and `Player.m_localPlayer` refer to, plus the server flag and the clock, and puts them back on dispose, also when the test throws. Disposing also ends the frame (`UnityEngine.Object.EndOfFrame()`), so an object the test destroyed is gone before the next test. Builders install fresh objects: `WithWorld`/`WithTerrain`, `WithZdos`, `WithZoneSystem`, `WithScene`, `WithNetwork(server)` (a `ZNet` with no peers, a new `ZRoutedRpc` and a new Jotunn `NetworkManager`), `WithCommands` (an empty console-command table), `WithLocalPlayer(position)` (none by default, as on a dedicated server), `AsServer`, `RegisterHeightmap` (zones add up; loading a zone again replaces it), `UnloadHeightmap` and `CaptureLog`.

```csharp
using var world = new ValheimWorldScope().WithTerrain(new PlaneTerrain(30f)).WithZdos().WithNetwork(server: true);
```

It restores references, not contents. Nothing is deep-copied, so changing an object the scope did not install (adding a peer to the `ZNet` that was already there) outlives the test; install a fresh one with a builder instead. A mod's own statics are not the scope's: reset them in the test.

Limits: the doubles model only the behaviour listed above and the members mod logic has needed so far. Anything else is a plain field or a no-op, not the game. Unity objects carry only the `ZNetView` and `WearNTear` components and have no physics or rendering. Terrain is the rebuild and the compiler, not the game's mesh. Networking is in-process: peers connect only through linked socket doubles, and RPCs follow the delivery and routing rules above without real timing, loss or reordering. Test what the game does natively with `Valheim.Testing.Game` against a real server.

### Global keys, terrain modifier order and location placement

These doubles model world state that Valheim 1.0.16 keeps and mods read or add to. Their tests derive the expected values by hand from the game's rules.

Global keys (`ZoneSystem`):
- `SetGlobalKey` and `RemoveGlobalKey` (by `GlobalKeys` member, `GlobalKeys` and a float value, or string) are routed calls to the server. On the server the change applies at once and the whole list is broadcast to `Everybody` as the `GlobalKeys` routed call. A client changes nothing locally: its call goes out through `ZRoutedRpc.instance`, and each broadcast replaces its whole list, so a key only the client added (with the game's private `GlobalKeyAdd`, public here) is gone. Deliver a broadcast to a client with `ZRoutedRpc.instance.Deliver(serverId, "GlobalKeys", keys)`.
- The handlers exist only after `ZoneSystem.Start()`, which the game runs when the game scene starts, with `Game.Start`. The server registers `SetGlobalKey` and `RemoveGlobalKey`, a client `GlobalKeys`. A key set on the server before that is dropped without an error. `OnNewPeer(peer)` sends a new peer the whole list.
- Keys are stored lower case as `name` or `name value`, split at the first space. Setting a name again replaces its value. `GetGlobalKey(name)` finds a key by name in any case, whatever its value; `GetGlobalKey(key, out float)` reads the value; `GetGlobalKeyExact(line)` needs the whole line. Any string is a key. Members of `GlobalKeys` before `NonServerOption` are world modifiers.
- A save keeps the other keys (`SaveGlobalKeys`, `LoadGlobalKeys`). The server writes world modifiers into the world's starting keys (`WorldStartingGlobalKeys`, null when no world is loaded, as on a client), and `SetStartingGlobalKeys` reads them back after a restart.

Terrain modifiers (`TerrainModifier`): `Awake()` puts a modifier in the live list and `OnDestroy()` takes it out, as when the object spawns and goes. `TerrainModifier.GetAllInstances()` returns the list in the order a heightmap applies it:
1. Non-player before player (`m_playerModifiction`).
2. Lower `m_sortOrder`.
3. Earlier creation time. It is read at `Awake` from the ZDO's `terrainModifierTimeCreated` long, so it survives a save and restart. 1.0.16 writes that value only when it converts a world saved before its ZDO rework, so a modifier placed since reads 0 and sorts before a converted one.
4. Smaller squared distance of the whole position from the origin, height included. At the same ground distance, a higher modifier comes later, so a sort by ground distance alone gets the order wrong.

The list is sorted again only when a modifier joins or leaves. The game's sort is unstable; a full tie keeps joining order here. `Heightmap.ModifiersInApplyOrder()` lists the enabled modifiers that reach a zone (radius + 4 m, as `TerrainVSModifier`). The game applies the zone's terrain compiler after them. What a modifier does to the heights is not modelled. `ValheimWorldScope.WithTerrainModifiers()` starts a fresh live list, and the scope puts the previous one back.

Locations (`ZoneSystem.ZoneLocation`) have the game's placement fields and defaults. `m_minAltitude` and `m_maxAltitude` are metres above the water level (y = 30), not absolute heights. `ZoneLocation.AltitudeAboveWater(y)` and `IsAltitudeAllowed(y)` evaluate them as the placement does (both ends allowed; 30 m is fixed, not read from `m_waterLevel`). A definition written with absolute heights passes on a flat world near 30 m and fails elsewhere. `SlopeRotationYaw(downhill)` is the yaw a slope-rotated location gets: local +Z faces downhill, rounded to 22.5 degrees. `CheckPlacement(x, z, world, terrainDelta)` returns the first rule a point fails: origin distance, biome (the default `None` matches no biome), altitude, distance from the centre, then terrain delta. It does not check the biome area, forest, similar locations, vegetation or zone occupancy. Nothing generates or places locations.

### Peer connections and the join handshake

These doubles add global types (`ISocket`, `SocketDouble`, `ZSteamSocket`, `ZPlayFabSocket`, `ZRpc`, `SyncedList`, `OnlineBackendType`) and namespaced ones (`Steamworks.CSteamID`, `Splatform.Platform`, `Splatform.PlatformUserID`). A test project that already has its own stubs with these names gets duplicate-definition errors; delete the stubs, or add members through `partial` instead.

`ZNetPeer.m_rpc` (`ZRpc`) and `ZNetPeer.m_socket` (`ISocket`) let a test run a mod's handshake or admin check. `SocketDouble.Link(a, b)` joins two socket ends in-process: what one end sends arrives at the other when that side's `ZRpc.Update(dt)` (or `ZNet.UpdatePeers(dt)`) runs, so the test decides which side handles what, and when. `ZRpc` follows the game (1.0.16):
- `Invoke` writes the method's hash and the arguments (the routed RPCs' type table) and sends them; nothing is sent once the connection is closed. `Register` replaces an earlier handler of the same name. A call without a handler is ignored and kept in `Dropped`.
- A package that ends before the handler's parameters are read stops `Update` with `ErrorCode.IncompatibleVersion`, and `ZNet` then reports `ErrorVersion`. That is what a peer sending fewer fields than the handler reads causes. An exception inside a handler, including one from reading past the end of a `ZPackage` argument, is only logged, as in the game; `Exceptions` keeps it.
- Pings go out every second of `Update` time, and the connection closes after 30 s without a reply. `Deliver(method, args)` handles one call as if it had arrived.

`ZNet` runs the game's join handshake between two `ZNet` objects: the test's server is `ZNet.instance`, and the client is a second `ZNet` with its own `Uid`. Call `OnNewConnection(peer)` on each side, then `UpdatePeers(0f)` on each in turn. The server refuses with the game's `Error` codes, and the client's `Status` becomes the matching `ZNet.ConnectionStatus`: 3 `ErrorVersion` for another network version (`NetworkVersion`, 40 in 1.0.16), 8 `ErrorBanned` for the ban or permit list, 9 `ErrorFull` at ten players (an approximation: the game counts its player list, which it refreshes on joins and leaves and which includes the host unless the server is headless; the double counts the ready peers, plus the host when it has a local player), 6 `ErrorPassword` (`SetServerPassword`, `JoinPassword`) and 7 `ErrorAlreadyConnected`. A peer that joins becomes ready under its id. `OnNewConnection` and `RPC_PeerInfo` are public here, so a test applies a mod's patch the way Harmony would: it calls the prefix before `OnNewConnection`, and re-registers `"PeerInfo"` on the peer's `m_rpc` with a handler that runs the prefix and then `RPC_PeerInfo`. [HandshakeTests](../tests/Valheim.Testing.Doubles.Tests/HandshakeTests.cs) has a mod version check that refuses a peer whose version message has not arrived by the time its peer info does. When the client sends its version from its `OnNewConnection` prefix, as the modding wiki's version-handshake example does, that message goes out before the game's ServerHandshake on the same ordered connection, so it always reaches the server before the peer info and the check works. It only turns away a correct client when a mod sends its version later (for example in reply to the server's); a check that holds the peer info until the version arrives lets that client in. Not modelled: Steam session tickets and PlayFab authentication (every peer passes), invite keys, the world data in the server's peer info, and the game's other connection RPCs. The connection status is per `ZNet` here; it is static in the game.

### Steam and crossplay identities

`ZSteamSocket(steamId)` and `ZPlayFabSocket(platformUserId, remotePlayerId)` report a peer as the game does. On a Steam socket, `GetHostName()` is the bare SteamID64 (`76561198000000002`). On a crossplay (PlayFab) socket it is the platform-prefixed id (`Steam_76561198000000002`, `Xbox_2535…`), and the end point is `playfab/` and the PlayFab entity id. A mod that casts `peer.m_socket` to `ZSteamSocket` therefore throws `InvalidCastException` for a crossplay peer, and inside an RPC handler the game only logs it. Check admins with `ZNet.instance.IsAdmin(rpc.GetSocket().GetHostName())`. It matches as the game does: a Steam id matches `Steam_<id>` or the bare id, another platform's id only `<Platform>_<id>`, and the id's display form (`V_<id>`) matches too. Comparing the host name with the list's lines misses a Steam peer listed as `Steam_<id>`. `m_adminList`, `m_bannedList` and `m_permittedList` are `SyncedList`s; `Load(text)` reads a list file as the game does, skipping empty and `//` lines and trimming nothing. `ZNet.OnlineBackend` (the game's static `m_onlineBackend`) selects Steam or PlayFab for the handshake. A PlayFab connection is compressed once the versions match (`Compressing`). The sockets check no message sizes; routed RPCs keep Steam's 512 KiB limit on Steam only, and PlayFab's own send limits are not modelled. `Splatform.PlatformUserID` parses and formats the ids.

### Components, lifecycle, registries, roles and config (Doubles preview 5)

These doubles extend the model above; where they apply, they replace the limit that Unity objects carry only a `ZNetView` and a `WearNTear`. Unity members use Unity 6 names (`FindObjectsByType`, `Rigidbody.linearVelocity`, `LightType.Rectangle`, `AnimatorUpdateMode.Fixed`). Unity 6 still compiles the older names with an obsolete warning (the game itself calls `FindObjectsOfType`), so the doubles keep a few of them as obsolete forwarders: `FindObjectsOfType`, `FindObjectOfType`, `Rigidbody.velocity`/`drag`/`angularDrag`, `LightType.Area` and `AnimatorUpdateMode.AnimatePhysics`.

Unity objects:
- `GameObject.AddComponent`, `GetComponent(s)` (by base type or interface), `GetComponent(s)InChildren`, `GetComponentInParent` and `TryGetComponent` work on any component. Without `includeInactive`, only objects active in their hierarchy count.
- `transform` is a hierarchy: `SetParent` (keeping the world position by default), `parent`, `GetChild`, `Find("a/b")`, `root`, `localPosition` and `localScale`. A child's world position is its parent's plus the parent's scale times its local position. Rotation is not composed: under a rotated parent a child's world position and `TransformPoint` are wrong, `rotation` is simply stored, and collider bounds ignore the object's own rotation.
- `SetActive` and `activeInHierarchy` work as in Unity.
- MonoBehaviour messages are called by name, private or public, as Unity calls them. `Awake` runs when a component is added to an object active in its hierarchy, or when the object later becomes active; a prefab kept under an inactive parent therefore wakes only in its copies. Then come `OnEnable`, and `Start` before the first `Update`. `Update` and `LateUpdate` run once per frame, `OnDisable` on disable, deactivation or destruction, and `OnDestroy` while the object is still alive. `UnityEngine.Object.RunFrame(deltaTime)` runs one frame: it advances `Time.time` and `Time.frameCount`, resumes coroutines (`yield return null`, `WaitForSeconds`, `WaitForEndOfFrame`, nested routines, `WaitUntil`/`WaitWhile`), runs due `Invoke`/`InvokeRepeating` calls, then ends the frame. Unity logs an exception thrown by a message and carries on; here it reaches the test.
- `Object.Instantiate` copies a GameObject with its components and children using Unity's rules for serialized fields. Public and `[SerializeField]` fields are copied; lists, arrays and `[Serializable]` classes are copied too, so each copy has its own; references into the copied hierarchy point at the copy. Other fields (dictionaries, delegates, private state) get the constructor's values. A copy keeps the prefab's name, where Unity appends "(Clone)". `Utils.GetPrefabName` handles both, but code that matches instances by `name + "(Clone)"` (the game's SpawnSystem counts creatures that way) finds none here. An `Instantiate` inside a woken `Awake` is independent of the one that woke it.
- `FindObjectsByType`, `FindFirstObjectByType` and `FindAnyObjectByType` search components added with `AddComponent` or made by `Instantiate`. `ValheimWorldScope.WithScene()` starts an empty set.
- Behaviour, not only fields, for: colliders (`Box`, `Sphere`, `Capsule`, `Mesh`) with world `bounds` that are empty while the collider is off, `ClosestPoint` and `attachedRigidbody`; `Renderer.material`, which copies the shared material on first use as Unity does ("stone (Instance)"), and `MeshFilter.mesh` likewise; `Mesh` bounds recalculated when triangles are set, with out-of-range indices refused; `Animator` parameters, the same by name or by `Animator.StringToHash`; `Canvas.rootCanvas` and `ForceUpdateCanvases`; `Light` defaults. `UnityEngine.Random` uses Unity's ranges and is seeded with `Random.InitState`, but its sequence is not Unity's. `UnityEngine.Debug` writes to the log capture. Nothing is simulated or drawn.

Valheim (written from how 1.0.16 behaves):
- `ObjectDB` indexes `m_items` by name hash in `Awake` and `CopyOtherDB`. It uses a dictionary's `Add`, so a second item with the same name throws on the next `UpdateRegisters`; `FindDuplicateItems()` reports duplicates first. `ValheimWorldScope.WithObjectDB(items, recipes, effects)` declares the game's own content. `LoadMainMenuObjectDB()` runs the main menu's pass: `Awake` on an empty database, then `CopyOtherDB` takes the prefab's lists themselves, so a mod's additions stay in them for the next menu load. `LoadWorldObjectDB()` runs a world load's pass from the original content. `ObjectDB.AwakePostfix` and `CopyOtherDBPostfix` run where a Harmony postfix would. A registration without a guard ends up in the list twice at the second menu load; the tests show both the guarded and the unguarded case.
- `ZNetScene.Awake` indexes `m_prefabs` and `m_nonNetViewPrefabs` in `m_namedPrefabs` by name hash. A duplicate throws, naming both prefabs; `FindDuplicateHashes()` reports duplicates first. `GetPrefab(name|hash)` and `HasPrefab` read the index, and `AddPrefab` registers at once.
- In the game both registries hold every registered entry and look it up directly. For a registration another mod or a later pass has still to make, a test can hold entries back (`MarkNotYetRegistered(names)`, then `FinishRegistering()`): until then `GetPrefab`/`GetItemPrefab` return null for them and record the lookup in `EarlyLookups`, while `HasPrefab` and the index are untouched.
- `Utils`, including its quirks: gzip `Compress`, ordinal `CustomStartsWith`, `FindChild`, `GetEnabledComponentsInChildren`, `IterateHierarchy`, and `RoundToInt`/`FloorToInt`, which shift by 64000 in float and truncate: halves round up (`RoundToInt(2.5f)` is 3, `RoundToInt(-1.5f)` is -1), a value that close to an integer lands on it (`FloorToInt(0.999f)` is 1), and below -64000 the result truncates toward zero.
- `HeightmapBuilder` with its build thread made explicit. `IsTerrainReady` queues a build and `BuildQueued()` builds it. `RequestTerrainSync` hands a ready build out and removes it, so the next question queues again. Heights blend the corner biomes and are smoothed for a distant LOD.
- `DropTable`: chance, count, weights, `m_oneOfEach`, and a shared result list for `GetDropListItems`. Also `Inventory` stacking and slot order, `Container` default items added once and only by the owner, `Pickable` picked and enabled state in its ZDO, and `SpawnArea` weighted choice. The world level is 0 and the resource rate 1.
- `Localization` translates `$word`, `$KEY_<binding>` (from `ZInput.SetBinding`) and `$1`. It keeps the game's 100-entry cache, which `AddWord` does not clear. `SetLanguage` drops added words. `PlatformPrefs` is in memory.

Roles: `ValheimWorldScope` presets set the role flags mod code branches on and install a fresh world for it.
- `AsDedicatedServer()`: server and dedicated, with no local player.
- `AsHost(position)`: server, not dedicated, with a local player owned by this session.
- `AsClient(position)`.
- `AtMainMenu()`: no `ZNet`, `ZDOMan`, `ZNetScene` or world; a preview `Player` whose view has no ZDO, which `Player.GetAllPlayers()` lists as the game's does.
- `AddRemotePlayer(peerId, position, name)` adds another peer's player; on a server it is also a connected peer.
- `LoadPlugin<T>()` adds a `BaseUnityPlugin` as BepInEx's chainloader does, so its `Awake` runs.
- On a 1.0.16 dedicated server `PlatformPrefs` falls back to PlayerPrefs, so a plugin `Awake` that touches `Localization.instance` works there, and under `AsDedicatedServer()`. The failure some clients hit before 1.0 (0.221.10, Steamworks not initialized yet) is an explicit opt-in: set `PlatformPrefs.Unavailable` to a reason and every `PlatformPrefs` call, and so a first `Localization.instance`, throws until it is cleared (the scope restores it).

Config (BepInEx 5.4.23): `ConfigFile` with every `Bind` overload, `ConfigEntry<T>.Value`, `BoxedValue` and `SettingChanged`, `ConfigFile.SettingChanged`, `ConfigReloaded`, `Reload`, `Save`, `SaveOnConfigSet`, `TryGetEntry`, acceptable ranges and lists, and BepInEx's value text (escaped strings, invariant floats, enums by name, colours as RRGGBBAA). The files are on an in-memory disk. `FileText` (or `ConfigFile.WriteFile`) edits a file as a person would outside the game, and `Save` writes BepInEx's format, so a test can assert what gets written. `SettingChanged` fires once per real change and not for an equal value. As in BepInEx, binding with a non-default default already raises the file's `SettingChanged`. Values in the file for settings not bound yet are applied when bound. `ValheimWorldScope.WithConfigFiles()` gives a test an empty disk. BepInEx also converts `Vector2`, `Vector3`, `Vector4`, `Quaternion` and `Rect`; the doubles do not, so binding one throws here (add a converter with `TomlTypeConverter.AddConverter` if a test needs it). `Paths.ConfigPath` is not a real directory, so a mod's live-reload `FileSystemWatcher` on its config file cannot run under the doubles; call `Reload()` instead.

The doubles do not model config sync. ServerSync and Jotunn push the server's values to clients over RPCs. Test the mod's decisions by setting the entry values a client would receive, and test the sync itself natively: read the entry on the server and on a client after a join and after an admin change. Such a check must wait by re-reading the value with a bound, the way `LogWait` re-reads its file, and never on a single `FileSystemWatcher` event: the watcher fires several times for one save, and on Linux and on mounted or container filesystems it can miss events entirely.

Harmony: every `HarmonyPatch` constructor (argument types, `ArgumentType` variations, `MethodType`), `HarmonyPriority`, `HarmonyBefore` and `HarmonyAfter` record their target in `info`; `HarmonyMethod.Merge` combines a class's attributes. These follow HarmonyX 2.9.0, the Harmony BepInEx 5 ships. `new Harmony(id).PatchAll()` lists the patch classes it finds in `Patches`, and patches nothing: no target is resolved or checked, so a test around `PatchAll` passes even when the patch would never run or its target does not exist. Call the prefix or postfix yourself, and check targets natively. `Traverse` and `AccessTools` reach members of any visibility on real objects, as HarmonyX's do; a typed `Traverse` read casts and throws on another type.

A `ModuleInitializerAttribute` polyfill lets a net48 test project use `[ModuleInitializer]`. Define `VALHEIM_TESTING_NO_POLYFILLS` if the project declares its own.

Upgrading from preview 4:
- `GameObject.Position`/`Rotation` and `Transform.position` are properties now: `go.Position.x = 1` no longer compiles (CS1612), nor does passing them by `ref`/`out`.
- `GetComponent<T>` matches base types and interfaces: `GetComponent<MonoBehaviour>()` returns the `ZNetView`, `GetComponent<Component>()` the `Transform`.
- `ZNetScene` is a `MonoBehaviour` and `GetPrefab(string)` looks up by name hash. `HarmonyPatch` derives from `HarmonyAttribute` and is no longer sealed.
- New names that may clash with a project's own stand-ins: `Time.time`/`deltaTime`/`frameCount`, `UnityEngine.Random`, `Debug`, `Behaviour.enabled`, `Bounds`, `Collider` and its kinds, `Rigidbody`, `Renderer`, `Material`, `Mesh`, `MeshFilter`, `Light`, `Animator`, `Canvas`, `ScriptableObject`, `SerializeField`, `Sprite`, `Coroutine`, the Harmony attributes and `Traverse`/`AccessTools`, `BepInPlugin`, `BepInDependency`, `BepInProcess`, `BaseUnityPlugin`, `Paths`, `PluginInfo`, `LogLevel`, `Logger`, the `BepInEx.Configuration` types, `ObjectDB`, `ItemDrop`, `Recipe`, `StatusEffect`, `Piece`, `Utils`, `Localization`, `ZInput`, `PlatformPrefs`, `Inventory`, `Container`, `Pickable`, `SpawnArea`, `DropTable` and `HeightmapBuilder`. Delete the project's copy or rename it.
- A net48 test project that declares its own `ModuleInitializerAttribute` must delete it or define `VALHEIM_TESTING_NO_POLYFILLS`.

#### A doubles assembly named assembly_valheim

The doubles normally compile into the test assembly, so a type's assembly is the test assembly. Some mod code asks where a type comes from: it checks `type.Assembly.GetName().Name == "assembly_valheim"` to tell the game's components from other mods', or it resolves `Type.GetType("ZNetScene, assembly_valheim")`. For that code, put the doubles in their own class library named after the game's assembly, and give the test assembly access to its internals:

```xml
<!-- MyMod.Doubles/MyMod.Doubles.csproj -->
<PropertyGroup><AssemblyName>assembly_valheim</AssemblyName><RootNamespace></RootNamespace><LangVersion>10</LangVersion></PropertyGroup>
<ItemGroup><PackageReference Include="Valheim.Testing.Doubles" Version="[0.1.0-preview.5]" /></ItemGroup>
```

```csharp
// MyMod.Doubles/AssemblyInfo.cs
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("MyMod.Tests")]
```

The test project then references this project instead of the package. Partial additions to the doubles (a `Heightmap` hook, extra members) must live in the `assembly_valheim` project, because a partial type cannot span assemblies. A component the test assembly declares is then foreign, as another mod's would be.

## Static overrides (preview 6)

Mods keep settings, switches and caches in statics. A test that changes one must put back the value it found, not the default it expects; a hard-coded reset silently changes every later test when the default moves. `StaticOverride` in `Valheim.Testing` does this for static fields, static properties (private setters included) and environment variables:

```csharp
using var plain = StaticOverride.Set(() => MyMod.Meander, 0f)
    .And(() => MyMod.Enabled, false)
    .AndKeep(() => MyMod.Counter)            // changed by the code under test; restored anyway
    .AndEnvironment("MYMOD_DEBUG", "1");     // null removes the variable
```

Dispose restores in reverse order, also when the test throws. A restore that fails does not stop the others; the failures are reported together afterwards. A constant, a readonly field, a property without a setter or anything but `() => Type.Member` is refused when the override is created. Statics are process-wide, so tests that override them must not run in parallel with tests that read them.

## Grid dumps, rendering and parity (Valheim.Testing preview 7)

Offline audits read a world dump, draw it for review and check that two terrain sources agree. `Valheim.Testing` now does all three without new dependencies: `GridDumpTerrain` reads an evenly spaced CSV grid (the layout `cli_world_dump` writes) with exact node values, bilinear heights between nodes, nearest-node biomes and refusals for ragged files and out-of-range queries; `TerrainRenderer` writes a deterministic PNG of any `ITerrain` coloured by height or biome, with point and polyline overlays; `TerrainParity` compares two sources over an area within a tolerance and reports the worst points. The contracts are in [Shared terrain and zone fixtures](shared-world.md#read-a-grid-dump). A dump read offline is input, not evidence of native behaviour, and a rendering of it is for human review.

## Linux dedicated server (preview 11)

`ServerLaunch` builds an owned dedicated-server launch from a copied runtime directory. The platform comes from the runtime's contents: `valheim_server.exe` is Windows, `valheim_server.x86_64` is Linux, and both or neither is refused. It requires BepInEx's preloader and core and the platform's Doorstop loader (on Windows `winhttp.dll` and a `doorstop_config.ini` that enables Doorstop and targets the preloader, as for `ClientLaunch` below), so a broken or redirected install is refused instead of starting a vanilla server. A caller value for `DOORSTOP_ENABLED`, `DOORSTOP_TARGET_ASSEMBLY` or `DOORSTOP_DISABLE`, or a `--doorstop-*` argument, is refused on both platforms and the same variables are removed if inherited; `ServerLaunch` and `ClientLaunch` share this loader policy. These are checks of the install before launch; plugin pins after startup remain the proof that BepInEx ran. On Linux it also requires the executable's user-execute bit (not checked on a Windows host, which has no mode to read) and reproduces what BepInExPack_Valheim's `start_server_bepinex.sh` exports: `DOORSTOP_ENABLED`, `DOORSTOP_TARGET_ASSEMBLY`, `linux64` and `doorstop_libs` prepended to `LD_LIBRARY_PATH`, and `libdoorstop_x64.so` prepended to `LD_PRELOAD`. Existing entries are kept and no empty entry is added. `SteamAppId` defaults to 892970 on both platforms unless the caller sets it. The executable is started directly rather than through the script, so the owned-session PID check is unchanged. Arguments stay the caller's plan. Unlike `ClientLaunch`, a host may build the other platform's server launch: a Windows host builds a Linux launch for inspection (it cannot run it), and callers that launch check the host themselves.

`docker/linux-server` is a local Ubuntu 24.04 image: .NET 10 SDK, non-root user, the dedicated server from anonymous SteamCMD at build time and the hash-verified BepInEx pack. The manual `native-linux.yml` workflow builds it on the runner and runs `examples/LinuxServerSmoke`, which requires BepInEx's `Chainloader startup complete` and the server log's creation of the generated world followed by `Opened Steam server`, then stops the process. Only logs and the report are uploaded.

macOS has no dedicated server. `Detect` refuses a macOS game client (a `Valheim.app` bundle, a renamed bundle recognised by `Contents/MacOS/Valheim`, or a directory holding `Valheim.app`) when no server executable is present, and on a macOS host `RequireExecutable` and `CreateStartInfo` refuse either server before reading its mode. Both throw `PlatformNotSupportedException` naming the Linux container (`--platform linux/amd64`, experimental on Apple Silicon) or a remote Windows/Linux host. Detection itself still works on a Mac. Windows and Linux hosts are unchanged. The host is injectable internally, so every host's branches are unit-tested on any OS, and CI runs the tests on Windows, Linux and macOS.

The image pins `linux/amd64` and was verified on a Linux x86-64 Docker host on 28 September 2026 (server build 25527701): the smoke passed all five steps and `validate.cs` passed inside the container.

Limits: this boots a server with no ValheimCLI or mod plugins; it does not test save, networking or gameplay, and Steam's backend connection is recorded but not required. Unit tests cover detection, refusal and environment merging with fake files. Game clients in the cloud are not supported; for remote hosts over SSH see [Game hosts](#game-hosts-and-environment-profiles-preview-13). Images contain game files and must never be pushed or published.

## Game client launch (preview 11)

`ClientLaunch` is the client twin of `ServerLaunch`: it builds the `ProcessStartInfo` for one BepInEx game client from its install directory and starts nothing. The platform comes from the install's contents, never the host: `valheim.exe` is Windows, `valheim.x86_64` is Linux and a `Valheim.app` bundle (any case) is macOS. An install with more than one is refused, as is the bundle itself (pass the directory that holds it and BepInEx) and a dedicated-server runtime, which is pointed at `ServerLaunch`; `ServerLaunch` likewise names `ClientLaunch` when given a client. A client runs only on its own OS, so any other host throws `PlatformNotSupportedException`; there is no Wine or Proton support. `Detect` still works on any host.

| | Windows | Linux | macOS |
|---|---|---|---|
| Detected from | `valheim.exe` | `valheim.x86_64` (user-execute bit required) | `Valheim.app/Contents/MacOS/Valheim` (user-execute bit required) |
| Loader files required | `BepInEx/core/BepInEx.Preloader.dll` and `BepInEx.dll`; `winhttp.dll`; `doorstop_config.ini` enabling Doorstop and targeting `BepInEx\core\BepInEx.Preloader.dll` (any case, either separator, relative or absolute; another existing DLL is refused), in Doorstop 4 (`[General] enabled`, `target_assembly`) or Doorstop 3 (`[UnityDoorstop] enabled`, `targetAssembly`) form | BepInEx core; `doorstop_libs/libdoorstop_x64.so` | BepInEx core; `doorstop_libs/libdoorstop_x64.dylib` (BepInExPack_Valheim) or a universal `libdoorstop.dylib` (BepInEx's macOS build) with the requested slice |
| Started executable | `valheim.exe` | `valheim.x86_64` | `/usr/bin/arch -x86_64` or `-arm64`, then the bundle executable |
| Loader variables set | none; the proxy DLL reads its ini | `DOORSTOP_ENABLED=1`, `DOORSTOP_TARGET_ASSEMBLY`, `doorstop_libs` prepended to `LD_LIBRARY_PATH`, `libdoorstop_x64.so` prepended to `LD_PRELOAD` | `DOORSTOP_ENABLED=1`, `DOORSTOP_TARGET_ASSEMBLY`, the library's full path prepended to `DYLD_INSERT_LIBRARIES` and passed with `arch -e` |
| Architectures | x64 | x64 | x86_64 (default; Rosetta on Apple Silicon) or native arm64 |
| Also needed to run | desktop session, Steam | desktop session with `DISPLAY` or `WAYLAND_DISPLAY`, GPU, Steam | logged-in desktop session, Steam |

On every platform `-console` is added first unless the caller passes `console: false` or already includes it, other arguments follow unchanged, `SteamAppId` defaults to 892970 unless the caller sets it, and caller environment is applied first with existing search-list entries kept. A caller value for `DOORSTOP_ENABLED`, `DOORSTOP_TARGET_ASSEMBLY` or `DOORSTOP_DISABLE`, or a `--doorstop-*` argument, is refused because it would start a vanilla client; the same variables are removed if inherited. Linux and macOS install paths may not contain `:` (Linux also `;`), which their search lists cannot represent.

On macOS the game is started through `/usr/bin/arch` with an explicit slice, because a universal executable otherwise runs as the parent's architecture, which the Doorstop library may lack. `arch` is a protected system binary, so the kernel drops `DYLD_*` from its environment; `ClientLaunch` passes them with `-e` instead, and `arch` execs the game in place, so the started PID is the game's. It reads the Mach-O slices of the game and of each Doorstop library and inserts the first library with the requested slice, refusing with the slices it found otherwise. BepInExPack_Valheim ships only an x86_64 library, which is its supported route. A native arm64 launch needs the universal `libdoorstop.dylib` and a BepInEx core that runs natively on arm64, which this check cannot see. `LaunchArchitectures` reports the slices an install can be launched as.

Limits: `ClientLaunch` only builds the launch. A game client needs an interactive desktop session with a display, a GPU and a running, signed-in Steam client. A process started from a service, a scheduled task without a logged-in user or an SSH session usually has no display, so it fails or never shows a window. Starting it inside the user's session on another machine is [`InteractiveClient`'s job](#clients-in-a-hosts-desktop-session). For a Linux machine with an NVIDIA GPU, [`docker/linux-client`](../docker/linux-client/README.md) provides that session in a container: headless Xorg on the GPU, a Steam client logged in by QR approval, and `vt-launch-valheim`, which sets the same Linux loader environment as `ClientLaunch`. A native client system test (join a dedicated server on a prepared world, ClientSurfaceCheck and PaintCheck) has passed with it on a rented GPU VM. Unit tests cover detection, refusals, loader checks, slice selection and environment merging with fake install trees on all three platforms; no client has been started through `ClientLaunch` yet.

## Game clients in system tests (preview 12)

A system test that looks from a client describes it with a `ClientRunPlan` section and opens it with `ClientSession`. The [FullLifecycle example](../examples/FullLifecycle/README.md) uses both.

- `ClientRunPlan` holds the mode (`owned` or `attach`), the install, its `installPins` and launch arguments (owned only), the client's ValheimCLI host and port, strict plugin pins, the join address, a disposable local character, the name of the environment variable holding the join password, and the start, join and arrival timeouts. `Validate(params absentPlugins)` refuses a plan before anything starts: an owned client off this machine or without `installPins`, pins with a world key, no exact ValheimCLI pin, a listed plugin that is neither an exact MD5 nor `absent`, and any of `absentPlugins` not pinned `absent` (a server-only mod's claim is what a client without it sees). `MenuExpectations` and `WorldExpectations(worldUid)` are the strict pins at the menu and once joined. `"pinning": "none"` opts the client out ([Pins and the opt-out](#pins-and-the-opt-out)).
- `ClientSession.Launch(plan, output)` starts an owned client through `ClientLaunch` and returns it at its main menu, strictly pinned. It refuses first when the install's `BepInEx/patchers` holds anything the plan's `patchers` does not name, when its game build, BepInEx core or patchers differ from `installPins`, when something already listens on the client's CLI port (a command could reach a client it does not own), when no Steam client runs in this session, or when the password variable is missing from its own environment (the client inherits it). Startup waits on events, never on polling: ValheimCLI's listening line in this launch's BepInEx log (any of `StartupEvents.StartupFailures` ends it), then the `MainMenu` state push, each raced against the process exit, which ends startup at once with the exit code (and, when BepInEx wrote nothing, the path of `Player.log`). A failed startup stops the process it started. Disposing closes the connection and stops only that process, then keeps its BepInEx log and `Player.log` beside the evidence; `Logs` lists them for the teardown scan.
- `ClientSession.Attach(plan, output)` connects to an operator's client and verifies its menu pins; disposing never touches that process.
- Each session records every command to `client-commands.jsonl` (then `-2`, `-3`, and so on: evidence is never overwritten); an owned session also writes `client-process.json`.
- `ClientSession.Launch(plan, output, start, connect, ready)` takes the process, connection and readiness as functions, so a mod's integration tests can drive startup failures, timeouts and cleanup without a game.
- `PinnedFile` pins a plan's input file by full path and SHA256; `Verified()` rechecks the hash before use.

Limits: an owned client must run in the desktop session where Steam is running and signed in; `ClientSession` can tell only that Steam runs, not that it is signed in, and never signs in. It does not quit the client gracefully: leave the world first (`SessionControl.Leave`) so the character is saved, then dispose. Valheim 1.0 refuses a joined client's cheat commands whatever the server's admin list says; ValheimCLI's client setting `AllowOnServerClients = true` is what lets its test commands run there.

## Plan rules and client rounds (preview 13)

Mod runners repeat two kinds of glue, which the toolkit now owns. The [FullLifecycle example](../examples/FullLifecycle/README.md) uses both.

Plan rules on `ServerRunPlan` refuse a plan before anything is copied, with an `ArgumentException` that names the field and the fix:
- `RequireScenario(known...)`: the plan's `scenario` is one the runner knows.
- `RequireEnvironmentFlag(variable, purpose)`: an explicit fixture opt-in, set to exactly `1`. A missing variable, any other value (`true`, `1 `) and a key that differs only in case are refused.
- `EnvironmentChoice(variable, choices...)`: an optional choice, returned as null when absent (the mod's default) or as exactly one of the choices. Any other value, and a second key that differs only in case, are refused.
- `OnlyForScenario(settings, supplied, scenarios...)`: settings another scenario reads are refused rather than silently ignored.
- `CheckModeScenario(mode, map)`, used by the runner through `PinnedServerRunOptions.ModeScenarios` (for example `["prepare-bridge"] = ["bridge-respawn"]`): a listed mode runs only its scenarios, and unlisted modes run every scenario. A map key that is not one of the runner's modes is refused at once.

The toolkit's own tests cover the rules every pinned plan follows: output path, launch host, executable, pins, save root, Doorstop and token variables, and unknown fields. A mod's plan tests need only cover its own rules.

`ClientRounds` runs a persistence scenario's client rounds over `ClientSession`, `SessionControl` and `PlayerPlacement`. The mod supplies the per-round measurement as a delegate, and its step names where it wants its own (`OpenStep`, `ArriveStep`). For each of `Rounds` (default `first`, `after-restart`), the helper:
1. waits until the server accepts game connections;
2. joins (devcommands first, exactly once), verifies the client's world pins and waits for the world, which protects the player (`SessionControl.WaitForWorld`'s default);
3. arrives at `Arrival` if one is given, writing `{round}-arrival.json`;
4. runs the measurement, which records `{round}: ...` steps with `round.Step` and writes `{round}-{name}.json` with `round.Write`, refusing to overwrite existing evidence.

Between rounds come a confirmed save, the client's leave and a restart of only the owned server. The optional `afterRestart` check runs next (for example "the server still has the marker"). The last round only leaves. Every step goes through the `ScenarioReport`. `clientRounds` and `clientRoundsCompleted` in its provenance record how far the run got. The first failure stops the rounds and is rethrown: nothing runs after a failed save, restart or measurement, and that round's evidence stays. The client is closed in every outcome: an owned client's process is stopped, and an attached client is detached and left running. A failed close fails a passing run, but never hides an earlier failure. `Run` returns the server's actor after the last restart.

Limits: the helper orders and records the steps, and its integration tests use scripted transports and fake processes. The steps' native behaviour is that of the pieces it calls, which have their own native evidence. The helper itself has not been run against a game yet.

## Game-side adapter helpers (Valheim.Testing.Adapter, preview 1)

A mod's owned server runs need a small test adapter plugin in the server runtime: `OwnedServerSession` proves it started that very server by reading a `session` capability (token, process ID, save root, dedicated, readiness). `Valheim.Testing.Adapter` is a source package compiled into that adapter, which references ValheimCLI and the game (so this repository builds none of it). `TestExtension.Register(id, version, tokenVariable, modReady, registered, logError, commands...)` waits for ValheimCLI's extension API, then registers the mod's extension with the `session` capability and any test commands of its own; The session reports complete when the world is up and `modReady` returns true, and reports `acceptingConnections` separately: a dedicated server opens its game socket only when world generation finishes, on a first boot about 17 s after the world has loaded, and a join before that times out. `OwnedServerSession.WaitUntilJoinable(server, capability, timeout)` waits for it; call it just before the first join so the wait overlaps other work (server-side steps, an owned client's launch) instead of lengthening startup. `TestExtension.DevcommandsFlag()` reads the raw devcommands flag that ValheimCLI's extension gate uses. The example's [adapter](../examples/FullLifecycle/MyMod.TestAdapter/Plugin.cs) is the whole pattern in a dozen lines.

## Game hosts and environment profiles (preview 13)

A mod author often edits on one machine and runs the game on another: a Windows gaming PC, a Linux box, a container, a rented VM. macOS has no dedicated server at all. An **environment profile** says where each game process of a run lives, and an `IGameHost` does the work on that machine.

```json
{
  "hosts": {
    "linux-box":  { "kind": "ssh", "platform": "linux", "shell": "bash", "destination": "tester@linux-box.example",
                    "sshOptions": ["IdentityFile=~/.ssh/valheim_tests"], "lock": "/var/tmp/valheim-testing/lock" },
    "gaming-pc":  { "kind": "ssh", "platform": "windows", "shell": "powershell", "destination": "tester@gaming-pc.example",
                    "lock": "C:\\ValheimTesting\\lock" },
    "server-ctr": { "kind": "container", "platform": "linux", "shell": "bash", "container": "valheim-server", "user": "valheim",
                    "lock": "/home/valheim/lock" }
  },
  "server": { "host": "linux-box", "install": "/opt/valheim/server", "runtime": "/srv/valheim-testing/runs", "cliPort": 5577, "gamePort": 2456 },
  "clients": { "player": { "host": "gaming-pc", "install": "C:\\Games\\Valheim", "runtime": "C:\\ValheimTesting\\runs", "cliPort": 5578 } }
}
```

`EnvironmentProfile.Read(path)` refuses unknown fields and validates everything at once before anything starts: known kinds, platforms and shells (`powershell` is Windows PowerShell 5.1, Windows only; `pwsh` anywhere; no `bash` on Windows), ssh destinations and options as `SshGameHost` accepts them (no password, the port given once), absolute paths in the host's own style, a runtime directory outside the install, no server on a macOS host, one game client per host, no ValheimCLI port used twice on one host, and no two roles reached on the same port of this machine (a local host's or container's CLI port, an SSH role's `localCliPort`). A `local` host must describe this machine's platform. `CreateHost(name)`, `CreateServerHost()` and `CreateClientHost(name)` return the hosts. A profile describes one person's machines, so keep it beside the mod's tests, not in them; it holds no credentials. SSH uses keys or an agent only (BatchMode is always on, a password is never accepted), and the host key must already be known.

| Operation | `IGameHost` | Contract |
|---|---|---|
| Run a script | `RunAsync(script, variables, timeout)` | bash or PowerShell. The script travels on standard input, never on a command line, and each variable arrives as a quoted literal no shell interprets. A small wrapper on the host reports the script's own exit code, so a Windows login shell that turns every failure into 1 still returns the real code. |
| Take the host lock | `AcquireLockAsync(lockPath, owner, timeout)` | The exclusive creation of an owner file in the lock directory; exactly one run wins. Each acquisition claims as the given owner plus a unique id (`HostLock.Owner`), so two runs that pass the same owner never both hold the lock and only the holding handle releases it. Another claimant's lock throws `HostLockException` with its holder, at once: nothing waits or retries. An unproven claim names the claimant it tried, to check with before deciding what to do. Disposing the `HostLock` releases it and throws if the release cannot be proven. A lock left by a crashed run stays until a person removes it. |
| Ship a revision or files | `ShipRevisionAsync(repository, revision, dir, timeout)`, `ShipFilesAsync(localDir, dir, timeout)` | `git archive` of a commit (tracked files only, never the working tree) or a local directory, as a tar checked by SHA-256 on the host and extracted into a new directory; `SOURCE.txt` there records the commit, tree and hash. |
| Follow a log | `LogOffsetAsync(log, timeout)`, then `WaitForLogAsync(log, offset, success, failures, timeout)` | Event driven on the host (`tail -F`, or a file watcher under PowerShell); lines are matched here with .NET regular expressions, failure patterns first. Take the offset before the action whose line you await. The log may not exist yet. A log the game recreates at each boot is best given a fresh path (the run's own runtime copy) and offset 0. Expiry returns `TimedOut`; `EnsureMatched()` throws the same `WaitFailedException` and `WaitTimeoutException` as `LogWait`. |
| Fetch evidence | `FetchDirectoryAsync(dir, localDir, timeout)` | A tar of the host directory, hash and size checked on both ends, extracted into a new local directory; entries that would land outside it are refused. |
| Reach ValheimCLI | `OpenCliTunnelAsync(cliPort, readyTimeout, localPort = 0)` | See below. |

A host result is `Exited` with the script's code, `TransportFailed` (ssh exit 255, a Docker error or a missing executable, without the host's report), or `Unknown`. A deadline always gives `Unknown` with `TimedOut` set: the script may not have run, may have finished or may still be running. `Succeeded` and `Failed` are both false for it, and a lock claim that timed out is `HostLockState.Unknown`, neither yours nor free. Nothing is retried, because a mutation must never be repeated blindly; check with a read-only script before deciding what to do.

ValheimCLI listens on its host's loopback only, and stays that way. For an SSH host, `OpenCliTunnelAsync` starts `ssh -N -L 127.0.0.1:<local>:127.0.0.1:<cliPort>` with `ExitOnForwardFailure` and `GatewayPorts=no`; neither end is ever bound beyond loopback. A requested local port already in use is refused before ssh starts (a client would otherwise reach whatever listens there); 0 picks a free one. Checking the port and ssh binding it are separate steps, so another process can take it in between: ssh then exits (ExitOnForwardFailure) and the tunnel fails, but a readiness probe made before ssh exited could reach that process. ssh would also open the `LocalForward`, `RemoteForward` and `DynamicForward` entries an ssh config gives the host, possibly beyond loopback, and `ClearAllForwardings` would clear the tunnel's own forward too, so a host whose effective config (`ssh -G`) has any is refused. The tunnel is ready when a local connection succeeds, and disposing it stops that ssh process and no other. Connect as usual to `127.0.0.1:tunnel.LocalPort`, for example `StateWait.Connect(tunnel.Address, tunnel.LocalPort)`. A local host's CLI is its own port. A container's CLI is reachable only when the container shares the Docker host's network (`--network host`, Linux, a local Docker daemon): a published port reaches the container's own interface, never its loopback, so any other network mode is refused. Script runs pass `ClearAllForwardings=yes`, so an ssh config cannot add listeners to them.

| Host | Kind | Shell | Install (example) | Runtime and lock (example) | Server | Client | Tools the scripts use |
|---|---|---|---|---|---|---|---|
| Windows | `ssh` (OpenSSH Server; any login shell) or `local` | `powershell` or `pwsh` | `C:\Program Files (x86)\Steam\steamapps\common\Valheim dedicated server` | `C:\ValheimTesting\runs`, `C:\ValheimTesting\lock` | Yes | Only in the signed-in desktop session; an SSH session has none | `System32\tar.exe` (Windows 10 1803, Server 2019 or later); hashes use .NET, not `Get-FileHash`, which Windows PowerShell cannot load under PowerShell 7's module path |
| Linux | `ssh` or `local` | `bash` (POSIX login shell) or `pwsh` | `/opt/valheim/server` | `/srv/valheim-testing/runs`, `/var/tmp/valheim-testing/lock` | Yes | With a display ([docker/linux-client](../docker/linux-client/README.md)) | coreutils (`base64`, `mktemp`, `sha256sum`, `tail -F`), `tar` |
| macOS | `local` or `ssh` (Remote Login) | `bash` or `pwsh` | `/Users/<you>/Library/Application Support/Steam/steamapps/common/Valheim` | `/Users/<you>/ValheimTesting/runs`, `/Users/<you>/ValheimTesting/lock` | **No** (refused by the profile) | In the signed-in desktop session | `shasum`, BSD `tail` and `tar` |
| Linux container | `container` (`docker exec -i`) | `bash` | `/opt/valheim/server` ([docker/linux-server](../docker/linux-server/README.md)) | `/home/valheim/runs`, `/home/valheim/lock` | Yes | [docker/linux-client](../docker/linux-client/README.md) | as Linux |

Paths are literal: they arrive as quoted values, so spaces are fine, and they must be absolute because an SSH session's working directory is the user's home. A lock is per host, so two containers on one machine are two hosts. A bash host needs a POSIX login shell for SSH; a PowerShell host works under cmd.exe, bash or PowerShell.

CI checks every host kind: unit tests with fake processes cover the commands each kind starts, transport failures, timeouts (unknown outcome) and lock contention; the same end-to-end checks (literal values and exit codes, a timeout, lock contention, shipping a revision and files, fetching them back, following a log from an offset) run through a real local bash, Windows PowerShell and pwsh on every CI OS, and the `game hosts` job runs them over a real ssh to a throwaway sshd on localhost (bash and pwsh sessions, the loopback tunnel, an unreachable port) and into a throwaway container on the host network.

Limits: this is the host layer. `ServerLaunch`, `ClientLaunch`, `OwnedServerSession` and `ClientSession` still start and own processes on this machine; driving them on a remote host through a profile is a follow-up (a client can be started in a remote desktop session with `InteractiveClient`, below). A server-only native run on a Linux container host driven over SSH from another OS has **not** been done yet. An `ssh` client on Windows is the system OpenSSH client; Windows as an SSH *host* has been checked only through the local Windows PowerShell path in CI, not over a real SSH session.

### Clients in a host's desktop session

A game client needs the logged-in user's desktop session: its display, GPU and signed-in Steam client. A process started over SSH or from a service has none of these, so `ClientSession.Launch` cannot start a client on another machine. `InteractiveClient.StartAsync(host, launch, launchDirectory, timeout, display)` starts one inside the host's existing session and returns an `InteractiveClientProcess` identified by process ID and start time, the identity an owned session checks against its adapter's reported process ID.

```csharp
var launch = HostClientLaunch.Create(ClientPlatform.Windows, @"C:\Games\Valheim", ["+connect", "server.example:2456"],
    secretVariables: [plan.PasswordVariable!]);
await using var client = await InteractiveClient.StartAsync(profile.CreateClientHost("player"), launch,
    @"C:\ValheimTesting\runs\run-42\client", TimeSpan.FromMinutes(2));
// Then wait for ValheimCLI's line with WaitForLogAsync and connect through OpenCliTunnelAsync.
```

- `HostClientLaunch.Create(platform, install, arguments, environment, secretVariables)` is what `ClientLaunch` builds for a Windows or Linux install, as data: `-console`, `SteamAppId`, Doorstop's variables on Linux (`LD_LIBRARY_PATH` and `LD_PRELOAD` put in front of the session's own values), inherited Doorstop variables removed, and Doorstop overrides refused. The install is a path on the host; its game executable and BepInEx loader files are checked there before anything starts.
- **Windows** (a `powershell` or `pwsh` host): a scheduled task registered for the host user with "Run only when user is logged on" (an interactive token; no password is given or stored), under a unique name per launch (`ValheimTesting-client-<id>`). Starting it runs a small launcher in the desktop session, which starts the game and records its process ID and start time. The task is always removed again, whatever happened; if the removal cannot be proven, the client it started is stopped and the start fails with the `schtasks /Delete` command to run. A powershell window may flash once as the task starts.
- **Linux** (a `bash` host): `LinuxDisplay(display, waylandDisplay, runtimeDirectory, xAuthority)` names the existing session: a local X display (`:0` by default; Valheim draws through X, so a Wayland session needs its XWayland display), WAYLAND_DISPLAY and XDG_RUNTIME_DIR (`/run/user/<uid>` when it exists), XAUTHORITY (`$XDG_RUNTIME_DIR/gdm/Xauthority` or `~/.Xauthority` when not given) and the session bus. The game starts in its own session (`setsid`) under a small recorder that writes its process ID and later its exit code, so the end of the SSH session does not reach it. The host user must be the display's user. On a system whose logind kills a user's processes when the SSH session ends (`KillUserProcesses=yes`), the client ends with it.
- **docker/linux-client**: a `ContainerGameHost` for the container with user `steam` and `LinuxDisplay.ClientContainer` (`:0`). The Steam client must be signed in first (`vt-steam`, as in its README); this helper does not wait for Steam's "Logged On" as `vt-launch-valheim` does.
- **macOS**: not supported from another machine. A process started over SSH is not in the logged-in user's GUI session and cannot reach its Steam client; `HostClientLaunch.Create` refuses it. Run the runner inside the session and use `ClientSession.Launch`.

The host refuses before anything starts, with `InteractiveSessionException`: `NoSession` when the user has no desktop session there (Windows: no process of the user in a session other than 0; more than one session is refused too, because the task could start in either), or when the X display is missing or refuses the user (checked with `xdpyinfo` where it is installed); `NoSteam` when no Steam client (`steam.exe`, or a Linux process named `steam`, on that display when its environment names one) runs as the user in it. A missing install file, an existing launch directory or a host of the wrong shell is refused as well. `StartAsync` returns once the game process exists, not when it is ready. `StopAsync` and disposal kill only that process, and only while its start time still matches, so a reused process ID is never touched; `WaitForExitAsync` waits on the host. `InteractiveClientProcess` is an `IServerProcess`, so it can be given to `ClientSession.Launch(plan, output, start, connect, ready)`.

The launch directory keeps the evidence: the launch spec (no secret values), the recorded identity, the game's standard output (Linux) or the launcher's error (Windows). Environment values given to `HostClientLaunch` are evidence. A secret, such as the join password named by `ClientRunPlan.PasswordVariable` or an account password, is named in `secretVariables` instead: its value is read from this process's environment at launch (a missing one is refused first), never goes on a command line and never enters the composed script, which the host's wrapper keeps in a temporary file while the script runs (and which a killed wrapper, for example after a dropped SSH connection, can leave behind). It follows the script on standard input as a line of its own, which the wrapper holds in memory and hands to the script as the variable `VT_SECRETS`; the script clears it at once. On Linux it is then exported into the game's environment, where the host user and root can read it (`/proc/<pid>/environ`). On Windows it reaches the desktop session through a `vt-secrets-<id>` file in the user's own temporary directory, which the launcher deletes as it reads it and the start script deletes again when it ends; a start killed in between can leave that file (and the task, named in the error) behind. It is redacted from every reply and exception.

### Steam account leases

Steam allows one running session per account, and every client needs an account that owns Valheim. With several clients or test machines, a **pool file** hands out accounts without collisions. It names accounts only:

```json
{
  "pool": "valheim-clients",
  "leaseDirectory": "/var/tmp/valheim-testing/leases",
  "leaseMinutes": 120,
  "steamGuard": "signed-in",
  "accounts": [
    { "name": "vt_client_one", "host": "gaming-pc" },
    { "name": "vt_client_two", "host": "linux-gpu" }
  ]
}
```

```csharp
var pool = SteamAccountPool.Read("steam-accounts.json");
await using var lease = await pool.AcquireAsync(profile.CreateHost("linux-box"), runId, TimeSpan.FromSeconds(30), clientHost: "gaming-pc");
lease.Record(report, "player"); // provenance: steamAccount.player = vt_client_one
```

- `AcquireAsync(leaseHost, owner, timeout, clientHost, leaseTime)` leases the first free account (for `clientHost`, or any host) and throws `SteamAccountLeaseException` with each holder when none is free; nothing waits or retries. Every run that shares a pool must use the same lease host and directory; any `IGameHost` works, including a `LocalGameHost` for one machine.
- A lease is the exclusive creation of the account's next numbered claim file under `<leaseDirectory>/<pool>/<account>` (a hard link, or a no-replace move under PowerShell), which exactly one claimer wins. Only the holder, proven by its own lease id, can renew or release it. An account is free when its latest claim is released or has expired by the lease host's clock, so a crashed run's lease ends after `leaseMinutes`. Renew a long run's lease well before `ExpiresUtc` (every third of the lease time, say); a lapsed lease is `Lost`, and the run must stop using the account. Disposing releases it and throws when the release cannot be proven. An unproven claim is `Unknown` and, if it happened, expires on its own. `ListAsync` shows who holds what.
- Reports and exceptions carry the account's name, never a credential. `Record` puts the name in the report's provenance, so it is public wherever the report is published; use account names you are content to show. `Parse` refuses a pool field that could hold one (`password`, `secret`, `token`, `sharedSecret`, `guardCode` and the like) by name, without repeating its value.
- **Credentials** come only from a secret store or CI secrets, as environment variables at launch: an account names its variable with `passwordVariable`, and a launch passes that name in `HostClientLaunch`'s `secretVariables`. A password never goes on a command line, in a plan or pool file, or in a log.
- **Steam Guard** has no default; `steamGuard` must say which applies. `signed-in`: a person signs each host's Steam client in to its account once ("Remember me"), and runs never sign in or need a password (a `passwordVariable` is refused). `approve-each-login`: a person approves each new sign-in in the Steam Mobile app or by its QR code, as [docker/linux-client](../docker/linux-client/README.md) does. For it the pool only checks that a `passwordVariable` is a valid variable name; the library never reads the variable or signs in, so whoever signs in reads it, for example as a `HostClientLaunch` secret variable. There is no automated Steam Guard code entry, and nothing here ever types a code.
- **Characters**: use a disposable local character for each account, staged on the host that runs it (`vt-stage-character` in the container). The game keeps local characters under the OS user's `characters_local` and Steam Cloud characters under the account; a test must never create or use a cloud character, and one OS user per account keeps each account's local characters apart.

CI covers both with fake processes (the commands and scripts each platform is sent, every refusal, a lost reply as an unknown outcome, a task that could not be removed, stopping by identity, contention and holder reports, renewal and release outcomes, and a canary secret that must not appear in any reply, exception, spec, argument list, report or process command line, with a negative control that finds the same value when it is passed as plain environment; the composed script, which the host writes to a file, counts as visible) and through real shells: the wrapper's secret hand-off through bash, Windows PowerShell and pwsh on every CI OS and over ssh and in a container in the `game hosts` job (the script receives each secret, and while it runs no file in the wrapper's temporary directory, the script and the upload, holds one; the negative control passes the same value as an ordinary variable and the same scan finds it in the script file); leases through bash, Windows PowerShell and pwsh on every CI OS (ten concurrent claims on three accounts give three distinct holders; a crashed holder's lease expires and the old holder can neither renew nor release the new one; a failed run releases its lease; old claims are pruned) and, in the `game hosts` job, over ssh and in a container; the Linux start on a real X display (Xvfb) with a stand-in Steam and game, locally and over ssh to localhost (the loader and secret reach the game's environment, no command line or evidence file holds the secret, stopping leaves a bystander alone and the recorder reports the kill); the Windows task start in the Windows runner's own console desktop session, where GitHub runs the job (a stand-in Steam and game: the game runs in that session under the recorded identity, the task is gone afterwards, no secret file is left, and stopping leaves a bystander with the game's name alone); refusals of a missing display on Linux and of a Linux client on a macOS host.

Limits: no Valheim client has been started through `InteractiveClient` yet, on Windows or Linux; a native join through it on a remote host, and the check that disposing stops only that client, are still to be done. The Windows task path has been exercised only on a CI runner, locally rather than over SSH, with stand-in processes. A lease store on a network file system has not been tested; it needs hard links (bash) or an atomic no-replace rename (PowerShell).

## Adapter fixture, command and Harmony helpers (Valheim.Testing.Adapter preview 2)

Game-side pieces that mods' test adapters kept copying, each checking the conditions under which the game would silently do nothing and throwing instead. The adapter source now also needs `UnityEngine.PhysicsModule` and BepInEx's `0Harmony` among its references. They are written against the Valheim 1.0.16 decompile and have not yet run in game.

| Helper | What it does |
|---|---|
| `Members.Field<T>`, `StaticField<T>`, `Method(type, name, parameterTypes)`, `Call` | Exact reflected access to members the shipped game keeps private (publicized references show them as public). A missing or mistyped member throws; `Call` rethrows the method's own exception. |
| `ZoneTerrain.IsZoneGenerated(zone)` | The game's own generated-zone record (private `ZoneSystem.IsZoneGenerated`); not whether the zone is loaded. |
| `ZoneTerrain.WaitForTerrain(zones, seconds, cancelled)` | Waits for the terrain builder's base heights, as the game does before spawning a zone; asking queues the build. Step through it from an extension command: `foreach (var step in ...) yield return step;` |
| `ZoneTerrain.SpawnTerrain(zone)` | A zone's terrain with a terrain compiler whose ZDO is created as the game creates ghost objects: saved, not in the scene. Server only; refuses a zone with loaded terrain, a loaded compiler or a saved compiler. Dispose releases the Unity objects; the ZDO stays. |
| `ZoneTerrain.HeightmapHeight`, `ColliderHeight` | The heightmap's nearest-vertex height (compiler changes included) and a vertical ray against the heightmap's own collider. Both throw rather than return 0 outside the terrain. |
| `ZoneTerrain.SaveTerrain(compiler)` | The compiler's private save, which the game skips unless the compiler is initialized and owned here; that throws. |
| `ZoneTerrain.DestroyZdo(zdo)` | Only the owner can; the game queues it and sends it with its next update. |
| `ZoneTerrain.ZoneObjects(zone, limit, include)` | Every saved object in a zone, loaded or not (private `ZDOMan.FindObjects`). More matches than `limit` throws instead of truncating; zones beyond ±255 are refused, because the game files them under one shared slot. |
| `FixtureGate.Refusal(enableVariable, tokenVariable)` | Fixture commands run only when the owned test process set the variable to `1` and has a session token. |
| `ConsoleAliases.Add(registration, (alias, command)...)` | Console names for extension commands, through `cli_extension`'s own dispatch. A taken name refuses the set; disposal removes only this call's own command instances. |
| `InstalledPlugin.Version(guid)`, `Command(name, guid)` | Whether a plugin is loaded, and a read-only capability reporting it. |
| `KeyValueReply.Parse(line)`, `Single(lines, prefix)` | `key=value` fields of a console reply, `'quoted values'` included. A repeated key, an `ERROR:` line, no reply or two replies fail. |
| `HarmonyCensus.Command()` | A read-only `harmony [owner]` capability listing every patched method with each patch's owner, kind, priority, index, before/after and patch method, from HarmonyX's own record. |

`Members`, `KeyValueReply` and `FixtureGate` use no game types and have unit tests. The rest compile in CI in `tests/Valheim.Testing.Adapter.CompileCheck`: the sources as a consumer compiles them (net48, C# 10, nullable, warnings as errors) against the real HarmonyX 2.9.0 that BepInEx 5.4.23 ships and against declared signatures of the Unity, game, BepInEx and ValheimCLI members they use. That proves they compile and call those signatures; it does not prove the game still has them or that the private members they reflect on exist, which only a native run shows.

Why a census: when a patch's target method is missing, HarmonyX 2.9.0 throws out of `PatchAll` ("Undefined target method"). BepInEx logs the error and the game carries on, but the rest of that plugin's `Awake`, including its later patch classes, never runs, so the mod stays loaded half-patched. On the runner side (Valheim.Testing.Game preview 13), `HarmonyCensus.Read(actor, "mymod.testing/harmony", owner).Check(owner, declared)` compares the census with the patches the mod declares (`new DeclaredPatch("Terminal::InitTerminal", "postfix", "MyMod.Plugin+RegisterCommands::Postfix")`; a target without a parameter list names every overload). `RequireApplied()` fails naming each declared patch that is not applied; other owners' patches on the same methods are reported beside it, never failed. `HarmonyCensus.OthersChanged(before, after, owner)` lists other owners' patches that a reload or unload removed or added. The toolkit's own reload path and adapter helpers never unpatch; an adapter that patches unpatches only its own Harmony ID (`harmony.UnpatchSelf()`), never `Harmony.UnpatchAll()`. [FullLifecycle](../examples/FullLifecycle/README.md) checks its mod's patch first.

`ExtensionReload` holds the steps of [ReloadCheck](../examples/ReloadCheck/README.md): `Install(artifact, deployed)` copies beside the deployed DLL and renames over it; `WaitForReplacement(actor, id, version, replacing, timeout, interval)` waits for a new live instance at that version; `WaitForRemoval` waits until none is live. Both re-read `cli_extensions`, which is read-only, since a registration announces itself nowhere else.

## Offline binding check (Valheim.Testing.Bindings, preview 1)

A game update that removes, renames or retypes a member breaks a mod only when the runtime first compiles a method that uses it: `MissingFieldException`, `MissingMethodException` or `TypeLoadException`, possibly hours into play (the modding wiki's examples are `Terminal.m_input` in 0.217.14 and `SEMan.HaveStatusEffect` in 0.218.15). Building against stale publicized assemblies hides the same break until then. `Valheim.Testing.Bindings` finds it from the built DLL in seconds, before any native run: it reads the mod with Mono.Cecil and resolves every game reference against the assemblies you supply, without loading or running either.

What is checked, the way the runtime binds it:

- Every type, field and method the mod names in method bodies, signatures, locals, catch clauses, base types, interfaces, generic constraints, explicit overrides, attributes (including `typeof(...)` arguments) and the module's reference tables. Properties and events are their accessor methods (`get_Level`).
- A field matches by name and field type; a method by name, static or instance, generic arity, return type and parameter types, with byref, arrays and their rank, pointers, generic instances and modifiers compared exactly. Members are looked for in the type and then its base types, so a member moved to a base class still binds. Nested types are resolved through their declaring types and type forwarders are followed into the target assembly.
- A reference that does not bind is **missing**, listed once with every mod method (or type, field or attribute) that uses it, and with what the assembly has instead when that is a clue: the other overloads, the field's new type, a property that became a field.
- A member or type that binds but is not accessible from the mod (private, internal, or protected used outside a derived type) is an **access** finding, a separate list. See below.
- References into an assembly you did not supply are counted per assembly as **not checked**, never reported as missing. If the lookup of a member reaches a base type in an assembly you did not supply (a game type's `MonoBehaviour` base, say), the member is still reported missing, with a note that it was not searched there.

Supply the game's managed directory with `--game-dir` (the client's `valheim_Data/Managed` holds `assembly_valheim`, `assembly_utils`, the Unity modules and the framework the game runs on; add `BepInEx/core` for BepInEx and Harmony) or individual files with `--game-file`, which are matched by the assembly name inside them, so a pinned copy may have any file name. `--only <name>` limits which assemblies from the directories are checked. `assembly_valheim` must be supplied whenever the mod references it, so a wrong path fails instead of checking nothing; `--require <name>` adds others.

**Publicized assemblies and access.** Mods commonly compile against publicized copies of the game assemblies, where every member is public, and reach private members at run time. Checked against the real assemblies, each such reference is an access finding even if nothing changed, so access findings never fail the check by default: missing references are what break. Each access finding says whether the mod carries `[assembly: IgnoresAccessChecksTo("<assembly>")]` for that assembly (some publicizer build tasks add it), which declares the access as intended: those are **info**, the rest **warning**. `--fail-on-access` fails on warnings only. Checking against the publicized copy the mod was built with still finds removals, but reports no access findings.

| Exit code | Meaning |
|---|---|
| 0 | Every checked reference binds (access findings may be listed) |
| 1 | At least one missing reference; with `--fail-on-access`, also an access finding without `IgnoresAccessChecksTo` |
| 2 | Bad arguments, an unreadable or missing file or directory, or a required assembly not supplied: the check is incomplete |

Run it in the job that builds the plugin, since both need the game's assemblies, and before anything launches the game:

```yaml
      # After the step that builds the plugin.
      - name: Check that game references still bind
        shell: bash
        run: |
          dotnet tool install Valheim.Testing.Bindings.Tool --version 0.1.0-preview.1 --tool-path .tools
          .tools/valheim-bindings MyMod/bin/Release/MyMod.dll --game-dir "$VALHEIM_MANAGED"
```

`VALHEIM_MANAGED` is wherever that job finds the game's `Managed` directory: a self-hosted runner's install, or the dedicated server from anonymous SteamCMD (app 896660), whose `valheim_server_Data/Managed` holds the server's own copies of the game assemblies; check a client mod against the client's where you can. As for any build, do not commit game assemblies or upload them as artifacts or public caches. The package version is this source's; use the newest published one.

From a test instead:

```csharp
var options = new BindingCheckOptions();
options.GameDirectories.Add(managedDirectory);
BindingReport report = BindingCheck.Check(modDll, options);
Assert.Empty(report.MissingRequired);
Assert.True(report.Binds, string.Join("\n", report.Missing.Select(f => $"{f.Member}: {string.Join(", ", f.UsedBy)}")));
```

Limits: only metadata references are checked. Members found by name at run time are not: Harmony's `[HarmonyPatch(typeof(T), "Method")]` and `AccessTools` lookups (`nameof` compiles to a string too), reflection, and Jötunn or prefab names. Neither are type-shape changes that fail when a mod type loads (a game interface gaining a member the mod's class must implement, a base class becoming sealed or gaining an abstract member) or generic constraint changes. An attribute whose arguments name an enum from an assembly that cannot be found is noted, and the types in its arguments are not checked. A pass says the checked references bind, not that the mod behaves correctly with the new game version. The tests build a miniature game in three versions and two mods at test time; the tool has not been run against a real Valheim update in this repository.

## Build tools for mods

Two tools in this repository are files a mod copies rather than packages. [`tools/test-runners`](../tools/test-runners/README.md) (`run-tests.sh` and its PowerShell twin) runs a test project on `net10.0` with `dotnet test` and on `net48` with the xunit console runner, under Mono on macOS and Linux and on .NET Framework on Windows, prints one result per framework and fails if either fails. Under Mono it runs the `net48` tests serially by default: parallel test collections have crashed Mono in native assembly loading. [`tools/game-references`](../tools/game-references/README.md) (`Valheim.GameReferences.props`/`.targets`) gives a mod's game-side projects their references from one `ValheimPath` and stops a build without the game with an error naming the missing folder or assembly; `UseValheimCli` adds ValheimCLI for an adapter, from an explicit `CliDll`. Unit-test projects use neither the game nor these files, which is what lets them run on hosted CI ([layout](adopting.md#a-layout-that-builds-on-hosted-ci)).
