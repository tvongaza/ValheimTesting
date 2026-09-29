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
| `Valheim.Testing.Doubles` | source package, compiled into the consumer: partial doubles of the Unity, Valheim, BepInEx and Jotunn types mod logic uses (see [Game doubles](#game-doubles)) |
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
| A game state | `StateWait` | Subscribes to ValheimCLI's state pushes on a connection used for nothing else, asks the current state once, then awaits `STATE_CHANGED` pushes with a pending read; nothing is sent while it waits. A push that arrives during the question counts. A dedicated server's loaded world is `InWorldNoPlayer`. A state is not mod readiness. The transport keeps no history: a wait sees the current state and pushes from its own subscription on, not pushes sent before it connected. |
| No event | `Check.Eventually` | Bounded fallback: re-observes a read-only source at an interval, for example an adapter's `complete` flag. |

```csharp
using var log = new LogWait(Path.Combine(runtime, "BepInEx", "LogOutput.log")); // before launching
// ... start the server ...
await log.WaitAsync(StartupEvents.CliListening, TimeSpan.FromMinutes(5), [LogWait.Literal("[Fatal")]);
```

`OwnedServerSession` waits on these when given `Events = new StartupEvents { CliLog = ..., Failures = StartupEvents.BepInExPluginLoadFailures, States = () => StateWait.Connect(host, port) }`: no connection before this boot's `Command server listening` line, then the world-loaded push, then the session probe. The process exit is always watched: a server that exits during startup fails it at once with its exit code and the last log line. `BepInExPluginLoadFailures` ends startup on BepInEx's own "Could not load [" and "Error loading [" lines, so a pinned runtime with a missing dependency fails at once instead of at the readiness deadline. Only the adapter's own readiness, which has no event, is re-probed at the poll interval. The startup deadline bounds every step, including a connection or command that blocks: each races the process exit and the time left, a connection that completes after startup gave up is closed, and the final pin verification gets only the time left (at most the command timeout). Without `Events`, connecting keeps its bounded retries. `GameActor.WaitForEnvironment` takes an event too, for example `(left, token) => log.WaitAsync(loadLine, left, cancellation: token)`: it rechecks the pins when the reload's line appears, not every 200 ms.

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

`PlayerPlacement` (preview 11; intro skipping preview 12) packages that procedure for any mod's client check:

- `SkipIntro(client, timeout)` ends a new character's first-spawn intro (the Valkyrie ride) as the menu's Skip does, or stops it before it starts, and waits for the player to respawn on the ground (`cli_skip_intro`, not a cheat command). It returns whether an intro was running; for a character that has spawned before it changes nothing.
- `Protect(client)` turns on god, ghost and debug modes (`cli_set_player_safety true`) and requires the game to read all three back; debug flying stays off.
- `Arrive(server, client, point, timeout)` first skips the intro (pass `skipIntro: false` to leave it), because the game silently drops a teleport during the Valkyrie ride and within 2 s of a spawn; it then waits for the player to stand still for 3 s, finds the server's only connected player with a character (none or several is refused), asks the server once to teleport it just above the point with the game's own teleport, and waits until the client's own observations show the player settled there. A teleport reply is never taken as arrival, and a timeout does not repeat the teleport.
- `RequireSupported(client, point)` takes three readings half a second apart, each of which must show the player grounded and stationary on the declared ground; a `SupportException` carries every reading.

`SessionControl.Join` (preview 12) turns the client's devcommands on before joining, because ValheimCLI refuses the join (a mutating extension command) until it is on; `EnableDevcommands()` does the same on its own. Both read the game's reply and toggle again if that turned it off.

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
- strict pins, with `worlduid` and an exact MD5 for every plugin.

The runner then:
1. refuses an existing output directory, and checks the host before copying;
2. copies and verifies the runtime and world, recording plan, runner and toolkit hashes, mode, platform and input hashes;
3. stops there for `validate`;
4. otherwise checks the CLI port and starts the owned session on the copies, with per-boot logs, recorded commands and `DedicatedStartupEvents`, then enables devcommands through the session capability and runs the scenario;
5. always stops only the owned server, writes `result.json` and `junit.xml`, and prints PASS (only for `run`), VALIDATED or PREPARED, or FAIL.

Ctrl+C and SIGTERM cancel the run. Options name the session capability and token variable, gate modes against the plan (`CheckMode`) and add provenance.

## Test fakes

`Valheim.Testing.Game.Fakes` tests scenario and session code without a game:
- `ScriptedTransport` answers strict pins, the extension listing, registered extension commands (in the real `EXTENSION_RESULT` envelope), confirmed saves and any command a test registers. It throws on anything unscripted, records every command, and `Actor()` returns a pinned `GameActor` on it.
- `FakeOwnedServer` launches `FakeServerProcess`es and answers the session probe and pins as an owned server with a session adapter. Switches make each failure happen: not ready, wrong PID, refused pins, exit on launch, refused connections, refused stop. Gates hold a connection or pin check open. `ConnectEntered`/`PinEntered` let a test inject a failure at exactly that stage, and `Events` records the lifecycle.
- `TempRuntime` is a runtime directory whose BepInEx log a test appends to or rewrites, as each boot does.

## Game doubles

For a complete package-consuming project that links real mod source and runs five tests, start with [ModWithTests](../examples/ModWithTests/README.md). The reference below describes the supported model and its limits.

`Valheim.Testing.Doubles` lets a unit-test project compile a mod's pure-logic source files (linked with `<Compile Include="../YourMod/Src/....cs" />`) without Unity, Valheim or BepInEx. It is a source package: its files compile into your test project and stand in for the game's types under their real names (`UnityEngine.Vector3`, `UnityEngine.Object` and `GameObject`, the global `ZDO`, `ZDOMan`, `ZNetView`, `ZNetScene`, `Heightmap`, `TerrainComp`, `ZoneSystem`, `WorldGenerator`, `ZNet`, `ZNetPeer`, `ZRoutedRpc`, `ZPackage`, `Terminal` and its console commands, `Player.m_localPlayer`, `BepInEx.Logging.ManualLogSource`, Jotunn's `CustomRPC`). So the test project must not also reference the game's assemblies. It needs C# 10 and works on net48 and modern .NET. Reference it with `PrivateAssets="all"` (it is a development dependency).

Every type is `partial`: add the members your mod calls in your own files. Keep only mod-specific behaviour there. Two `Heightmap` hooks carry a mod's terrain logic into the rebuild: `ModBaseHeight` (for example a biome blend) and `ModTerrainPass` (the seam a Harmony prefix on the game's rebuild uses).

The doubles copy the game where mod code depends on it, and their own tests check these points:
- `ZDOMan.DestroyZDO` is queued, so a destroyed ZDO stays visible to `FindObjects` until `ProcessDestroyed`.
- A destroyed `UnityEngine.Object` compares equal to null, as in Unity (see [Destroyed objects](#destroyed-objects)).
- A networked prefab instantiated during ghost initialisation leaves its ZDO and joins no live scene.
- `ZNetScene.Destroy` removes the object from the live scene at once and queues only ZDOs this session owns; a compiler saves only when owned.
- Rebuilt heights are clamped to the game's ±8 m.
- Heightmaps are loaded per zone and found by position: `Heightmap.FindHeightmap(point)` returns the loaded zone that holds the point (edges included) or null, `GetAllHeightmaps()` lists every loaded zone, and `TerrainComp.FindTerrainCompiler(pos)` returns that zone's compiler. A write aimed at the wrong zone, or at a zone that is not loaded, therefore misses as it would in the game.
- Zone ids narrow to `short`.
- Valheim 1.0's locations-generated flag is set from a save without raising the event.
- A routed RPC over Steam's 512 KiB limit fails. `ZRoutedRpc.Invoked` records every call with its target, and `ZRoutedRpc.Deliver(sender, method, args)` runs a registered handler as if a peer's call arrived. Jotunn RPCs are kept by name in `NetworkManager.Instance.Rpcs` and record what they send.
- Constructing a `Terminal.ConsoleCommand` registers it under its lower-case name. `Terminal.TryRunCommand` runs it with that terminal as `args.Context` and prints an unknown command or a failed failable action; `Terminal.Output` holds what was printed. Cheat, server-only and admin gating is not modelled; the mod's own checks still run.

`TerrainSnapshot.Of(compiler)` copies a zone compiler's level and smooth deltas, paint mask and both modified flags; `TerrainAssert` compares the compiler with it afterwards:
- `OnlyChangedWithin(before, compiler, (x, z) => ...)`: every change lies inside the footprint you declare, and the rest of the zone, flags included, is exactly as it was. It also fails when nothing changed, since then it proves nothing.
- `Unchanged(before, compiler)`: nothing changed at all.
- `SeamAgrees(west, east)`: two loaded neighbours agree on their shared vertices, rendered height and paint (rebuild both first). A zone written without its neighbour shows up here.

### Destroyed objects

`UnityEngine.Object` has Unity's overloaded `==`, `!=`, `Equals` and `bool` conversion: a destroyed object is equal to null and `if (obj)` is false. `is null`, `?.` and `??` skip the overload and see a live reference, as in Unity. Engine members of a destroyed object (`name`, `gameObject`, `GetComponent`) throw `UnityEngine.MissingReferenceException`; a component's own fields and methods (`WearNTear.m_health`, `ZNetView.GetZDO()`) still answer, as they do in Unity. Live objects and real null references compare as before, and objects stay distinct dictionary keys and list entries after they are destroyed.

`Object.Destroy` is deferred like Unity's end-of-frame destruction: the object stays alive until the test calls `UnityEngine.Object.EndOfFrame()` (it returns how many objects went). `DestroyImmediate` destroys at once. Destroying a `GameObject` destroys its `ZNetView` and `WearNTear` and removes it from `ZNetScene.Live`; destroying only a component makes `GetComponent` return null for it. Pending destroys are process-wide: call `EndOfFrame()` in the test, or in its `Dispose`, so none carry into the next test.

This catches the `?.` mistake the modding wiki warns about. Say a mod's `PieceHealth.Total(pieces)` adds up `piece?.GetComponent<WearNTear>()?.m_health ?? 0f`. This test then fails with a `MissingReferenceException`; once the mod checks `if (piece == null) continue;` before `GetComponent`, it passes:

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

`ValheimWorldScope` gives each test its own world. It records which objects `WorldGenerator.instance`, `ZDOMan.instance`, `ZoneSystem.instance`, `ZNetScene.instance`, `ZNet.instance`, `ZRoutedRpc.instance`, Jotunn's `NetworkManager.Instance`, the loaded heightmaps, the log capture, `Terminal.commands` and `Player.m_localPlayer` refer to, plus the server flag and the clock, and puts them back on dispose, also when the test throws. Builders install fresh objects: `WithWorld`/`WithTerrain`, `WithZdos`, `WithZoneSystem`, `WithScene`, `WithNetwork(server)` (a `ZNet` with no peers, a new `ZRoutedRpc` and a new Jotunn `NetworkManager`), `WithCommands` (an empty console-command table), `WithLocalPlayer(position)` (none by default, as on a dedicated server), `AsServer`, `RegisterHeightmap` (zones add up; loading a zone again replaces it), `UnloadHeightmap` and `CaptureLog`.

```csharp
using var world = new ValheimWorldScope().WithTerrain(new PlaneTerrain(30f)).WithZdos().WithNetwork(server: true);
```

It restores references, not contents. Nothing is deep-copied, so changing an object the scope did not install (adding a peer to the `ZNet` that was already there) outlives the test; install a fresh one with a builder instead. A mod's own statics are not the scope's: reset them in the test.

Limits: the doubles model only the behaviour listed above and the members mod logic has needed so far. Anything else is a plain field or a no-op, not the game. Unity objects carry only the `ZNetView` and `WearNTear` components and have no physics or rendering. Terrain is the rebuild and the compiler, not the game's mesh. Networking is in-process: no peers connect, and routed RPCs are only checked for size. Test what the game does natively with `Valheim.Testing.Game` against a real server.

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

## Linux dedicated server (preview 11)

`ServerLaunch` builds an owned dedicated-server launch from a copied runtime directory. The platform comes from the runtime's contents: `valheim_server.exe` is Windows, `valheim_server.x86_64` is Linux, and both or neither is refused. It requires BepInEx's preloader and core and the platform's Doorstop loader (on Windows `winhttp.dll` and a `doorstop_config.ini` that enables Doorstop and targets the preloader, as for `ClientLaunch` below), so a broken or redirected install is refused instead of starting a vanilla server. A caller value for `DOORSTOP_ENABLED`, `DOORSTOP_TARGET_ASSEMBLY` or `DOORSTOP_DISABLE`, or a `--doorstop-*` argument, is refused on both platforms and the same variables are removed if inherited; `ServerLaunch` and `ClientLaunch` share this loader policy. These are checks of the install before launch; plugin pins after startup remain the proof that BepInEx ran. On Linux it also requires the executable's user-execute bit (not checked on a Windows host, which has no mode to read) and reproduces what BepInExPack_Valheim's `start_server_bepinex.sh` exports: `DOORSTOP_ENABLED`, `DOORSTOP_TARGET_ASSEMBLY`, `linux64` and `doorstop_libs` prepended to `LD_LIBRARY_PATH`, and `libdoorstop_x64.so` prepended to `LD_PRELOAD`. Existing entries are kept and no empty entry is added. `SteamAppId` defaults to 892970 on both platforms unless the caller sets it. The executable is started directly rather than through the script, so the owned-session PID check is unchanged. Arguments stay the caller's plan. Unlike `ClientLaunch`, a host may build the other platform's server launch: a Windows host builds a Linux launch for inspection (it cannot run it), and callers that launch check the host themselves.

`docker/linux-server` is a local Ubuntu 24.04 image: .NET 10 SDK, non-root user, the dedicated server from anonymous SteamCMD at build time and the hash-verified BepInEx pack. The manual `native-linux.yml` workflow builds it on the runner and runs `examples/LinuxServerSmoke`, which requires BepInEx's `Chainloader startup complete` and the server log's creation of the generated world followed by `Opened Steam server`, then stops the process. Only logs and the report are uploaded.

macOS has no dedicated server. `Detect` refuses a macOS game client (a `Valheim.app` bundle, a renamed bundle recognised by `Contents/MacOS/Valheim`, or a directory holding `Valheim.app`) when no server executable is present, and on a macOS host `RequireExecutable` and `CreateStartInfo` refuse either server before reading its mode. Both throw `PlatformNotSupportedException` naming the Linux container (`--platform linux/amd64`, experimental on Apple Silicon) or a remote Windows/Linux host. Detection itself still works on a Mac. Windows and Linux hosts are unchanged. The host is injectable internally, so every host's branches are unit-tested on any OS, and CI runs the tests on Windows, Linux and macOS.

The image pins `linux/amd64` and was verified on a Linux x86-64 Docker host on 28 September 2026 (server build 25527701): the smoke passed all five steps and `validate.cs` passed inside the container.

Limits: this boots a server with no ValheimCLI or mod plugins; it does not test save, networking or gameplay, and Steam's backend connection is recorded but not required. Unit tests cover detection, refusal and environment merging with fake files. Game clients in the cloud are not supported, and remote hosts over SSH are the next step. Images contain game files and must never be pushed or published.

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

Limits: `ClientLaunch` only builds the launch. A game client needs an interactive desktop session with a display, a GPU and a running, signed-in Steam client. A process started from a service, a scheduled task without a logged-in user or an SSH session usually has no display, so it fails or never shows a window. Starting the plan inside the user's session, and anything remote, is left to a later host adapter. For a Linux machine with an NVIDIA GPU, [`docker/linux-client`](../docker/linux-client/README.md) provides that session in a container: headless Xorg on the GPU, a Steam client logged in by QR approval, and `vt-launch-valheim`, which sets the same Linux loader environment as `ClientLaunch`. A native client system test (join a dedicated server on a prepared world, ClientSurfaceCheck and PaintCheck) has passed with it on a rented GPU VM. Unit tests cover detection, refusals, loader checks, slice selection and environment merging with fake install trees on all three platforms; no client has been started through `ClientLaunch` yet.

## Game clients in system tests (preview 12)

A system test that looks from a client describes it with a `ClientRunPlan` section and opens it with `ClientSession`. The [FullLifecycle example](../examples/FullLifecycle/README.md) uses both.

- `ClientRunPlan` holds the mode (`owned` or `attach`), the install and launch arguments (owned only), the client's ValheimCLI host and port, strict plugin pins, the join address, a disposable local character, the name of the environment variable holding the join password, and the start, join and arrival timeouts. `Validate(params absentPlugins)` refuses a plan before anything starts: an owned client off this machine, pins with a world key, no exact ValheimCLI pin, a listed plugin that is neither an exact MD5 nor `absent`, and any of `absentPlugins` not pinned `absent` (a server-only mod's claim is what a client without it sees). `MenuExpectations` and `WorldExpectations(worldUid)` are the strict pins at the menu and once joined.
- `ClientSession.Launch(plan, output)` starts an owned client through `ClientLaunch` and returns it at its main menu, strictly pinned. It refuses first when something already listens on the client's CLI port (a command could reach a client it does not own), when no Steam client runs in this session, or when the password variable is missing from its own environment (the client inherits it). Startup waits on events, never on polling: ValheimCLI's listening line in this launch's BepInEx log (a plugin-load error ends it), then the `MainMenu` state push, each raced against the process exit, which ends startup at once with the exit code. A failed startup stops the process it started. Disposing closes the connection and stops only that process, then keeps its BepInEx log beside the evidence.
- `ClientSession.Attach(plan, output)` connects to an operator's client and verifies its menu pins; disposing never touches that process.
- Each session records every command to `client-commands.jsonl` (then `-2`, `-3`, and so on: evidence is never overwritten); an owned session also writes `client-process.json`.
- `ClientSession.Launch(plan, output, start, connect, ready)` takes the process, connection and readiness as functions, so a mod's integration tests can drive startup failures, timeouts and cleanup without a game.
- `PinnedFile` pins a plan's input file by full path and SHA256; `Verified()` rechecks the hash before use.

Limits: an owned client must run in the desktop session where Steam is running and signed in; `ClientSession` can tell only that Steam runs, not that it is signed in, and never signs in. It does not quit the client gracefully: leave the world first (`SessionControl.Leave`) so the character is saved, then dispose. Valheim 1.0 refuses a joined client's cheat commands whatever the server's admin list says; ValheimCLI's client setting `AllowOnServerClients = true` is what lets its test commands run there.

## Game-side adapter helpers (Valheim.Testing.Adapter, preview 1)

A mod's owned server runs need a small test adapter plugin in the server runtime: `OwnedServerSession` proves it started that very server by reading a `session` capability (token, process ID, save root, dedicated, readiness). `Valheim.Testing.Adapter` is a source package compiled into that adapter, which references ValheimCLI and the game (so this repository builds none of it). `TestExtension.Register(id, version, tokenVariable, modReady, registered, logError, commands...)` waits for ValheimCLI's extension API, then registers the mod's extension with the `session` capability and any test commands of its own; The session reports complete when the world is up and `modReady` returns true, and reports `acceptingConnections` separately: a dedicated server opens its game socket only when world generation finishes, on a first boot about 17 s after the world has loaded, and a join before that times out. `OwnedServerSession.WaitUntilJoinable(server, capability, timeout)` waits for it; call it just before the first join so the wait overlaps other work (server-side steps, an owned client's launch) instead of lengthening startup. `TestExtension.DevcommandsFlag()` reads the raw devcommands flag that ValheimCLI's extension gate uses. The example's [adapter](../examples/FullLifecycle/MyMod.TestAdapter/Plugin.cs) is the whole pattern in a dozen lines.
