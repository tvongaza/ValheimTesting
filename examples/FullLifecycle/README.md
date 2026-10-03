# One mod, every test layer

This example takes the mod from [ModWithTests](../ModWithTests/README.md) and tests the same feature at every layer, from a unit test to a real server and client, with a separate project per layer. Running the unit or integration tests never launches Steam or Valheim; only the system-test runner does, and only when you ask it to.

**The feature:** the mod adds a server command, `mymod_mark <x> <z>`, that places a wooden pole (`wood_pole2`) as a marker where the world generator's ground is at least 1.5 m above the sea, and refuses anywhere wetter. The decision is `DrySiteRule`, the very file ModWithTests tests. The marker is an ordinary saved object, so it should persist through a save and restart and appear on clients that do not have the mod.

| Layer | Project | What it shows | Needs |
|---|---|---|---|
| Unit / synthetic | [ModWithTests/MyMod.Tests](../ModWithTests/README.md) | The real decision on declared terrain, with game doubles | .NET 10 SDK |
| Controlled integration | [MyMod.IntegrationTests](MyMod.IntegrationTests/) | The real scenario and client lifecycle code against scripted game replies: startup failure, incomplete observations, lost replies, save failure, cleanup | .NET 10 SDK |
| Native system | [MyMod.SystemTests](MyMod.SystemTests/) with [MyMod](MyMod/) and [MyMod.TestAdapter](MyMod.TestAdapter/) | A disposable server copy and a real client: the mod's Harmony patch applied, join, strict pins, protect (by default) and place the player, exercise the feature, measure, confirmed save, restart, rejoin, measure again, stop what it started, report | A game install, Steam, a machine you may use |
| Human review | `review` in the plan | An optional look by a person, recorded beside the automated result and never part of it | A person |

`dotnet run scripts/validate.cs` runs the integration tests and builds the runner on every platform. The mod and the adapter compile against your game install, so they build only where you have one. They take its references from [tools/game-references](../../tools/game-references/README.md): set `ValheimPath` (or `VALHEIM_PATH`) to the game folder, and a missing install, BepInEx or ValheimCLI core stops the build with an error naming it.

## What belongs to the mod, and what to the toolkit

| The mod supplies | The toolkit supplies |
|---|---|
| The feature and its command (`MyMod/Plugin.cs`) | The owned server lifecycle: copies, startup events, identity handshake, restart, teardown, report (`PinnedServerRun`, `OwnedServerSession`) |
| The plan fields and their rules (`LifecyclePlan.cs`): which site is dry, which is wet, where the player stands | The client section and its rules (`ClientRunPlan`), and owned or attached clients (`ClientSession`) |
| The expectations and the scenario (`DrySiteScenario.cs`): one marker here, none there, still there after a restart | Session steps (`SessionControl`: devcommands, join, leave, readiness, protection once the world is ready), player placement (`PlayerPlacement`: intro, arrival, support), strict pins (`GameActor`) |
| A test adapter serving the owned-session identity and the Harmony census (`MyMod.TestAdapter`) | The adapter's registration, identity capability and census command ([Valheim.Testing.Adapter](../../src/Valheim.Testing.Adapter/Adapter/), compiled into the adapter) |
| The patches it declares (`DrySiteScenario.Patches`) | The census check (`HarmonyCensus`): each declared patch applied, other owners on the same methods reported |
| Integration tests of its scenario | The scripted fakes they use (`ScriptedTransport`) |

The scenario never calls `PlayerPlacement.Protect` itself: `SessionControl.WaitForWorld` protects the joined player (god, ghost and debug mode, read back) as soon as the world is ready, so the join step fails if the game does not confirm it, and the player is never moved unprotected. Fly stays off; the arrival and marker checks measure a player standing on the ground.

### Optional character start at the first site (preview)

For repeated native runs on the **same known world**, a disposable local character that has already visited that world can start at the dry arrival site. This is opt-in; the normal plan still teleports after joining.

Only a **registered** disposable character can be prepared. Register it once, in a store directory outside the game's folders; registration copies the save into the store and records the game's player ID, and never changes the original. After the character visits a new fixture world, take its newer save with `refresh-character`, which refuses a different character. Then prepare a separate copy while the client is stopped:

```sh
store=/absolute/path/to/test-character-store
evidence=/absolute/path/to/new-evidence-directory
world_uid=123456789
arrival_x=125
arrival_y=45
arrival_z=-380
fresh_name=mymod-test-001
# Once, for a character made only for tests:
dotnet run --project examples/FullLifecycle/MyMod.SystemTests -c Release -- \
  register-character "$store" mymodtester /absolute/path/to/characters_local/mymodtester.fch
mkdir -p "$evidence"
dotnet run --project examples/FullLifecycle/MyMod.SystemTests -c Release -- \
  prepare-character "$store" mymodtester "$world_uid" "$arrival_x" "$arrival_y" "$arrival_z" \
  "$evidence/$fresh_name.fch"
```

Replace those sample coordinates and UID with the pinned plan's values. The command validates the 1.0.16 save layout and hash, changes only the requested world's logout point, and refuses an unregistered name, a stored copy that is a different character from its registration, an existing output or an output inside a character folder or the store. It **does not install or launch** the copy. Use a fresh filename; `client.character` is that filename without `.fch`, never the display name. For an **owned** client, set `client.startAtCharacterSave` to `true` and include:

```json
"characterStart": {
  "preparedFile": "/absolute/evidence/mymod-test-001.fch",
  "sha256": "<PREPARED output's SHA256>",
  "charactersLocalDirectory": "/absolute/client-save/characters_local",
  "steamUserDataDirectory": "/absolute/Steam/userdata",
  "characterStore": "/absolute/path/to/test-character-store"
}
```

The runner checks the prepared bytes, world UID, exact arrival point, hash and that the store registers its player **before launch**, refuses a same-named local or Steam Cloud character, stages the copy, requires ValheimCLI to report `(<filename>, Local)` at selection, and removes only that copy and its game-made backups after stopping the owned client. This preview option requires a locally launched owned client; attached and remote-profile clients are refused until they have an equivalent owned staging boundary. A hosted fixture can use it with `directStart` and a declared dry `arrival` point. The first round checks the client's own support reading at `arrival`, without a teleport or fallback; a wrong start fails. Later rounds and zone-cycle movements still use teleports.

### Directly start a prepared client in its fixture world

For a clean registered test character that has **not** visited the fixture, use `prepare-character-new-world` with the fixture's verified UID and dry arrival coordinates in the command above. For a character that has already visited it, keep using `prepare-character`; the new-world command refuses an existing entry. Neither command creates a character or edits its original. In the owned client's plan, set `directStart: true` alongside `startAtCharacterSave: true` and the `characterStart` block. A dedicated-server run also sets `directStartWorldUid` to the server fixture UID. The runner waits for the server to accept joins *before* launching this client.

For the [hosted sample plan](MyMod.SystemTests/sample-plan-hosted.json), add an `arrival` site and set `client.hostWorld.local: true` for a local-only run (or leave it false for a listen server):

```json
{
  "arrival": { "x": 125, "z": -380, "ground": 45.2 },
  "client": {
    "directStart": true,
    "startAtCharacterSave": true,
    "character": "mymod-test-001",
    "characterStart": { "preparedFile": "/absolute/evidence/mymod-test-001.fch", "sha256": "<prepared SHA256>", "charactersLocalDirectory": "/absolute/client-save/characters_local", "steamUserDataDirectory": "/absolute/Steam/userdata", "characterStore": "/absolute/test-character-store" },
    "hostWorld": { "local": true }
  }
}
```

Those are additions to the full pinned plan, not a complete plan by themselves. Run `validate-host` first, then `host` with a fresh evidence directory. The owned client inherits a password variable if a dedicated join uses one; the startup request file holds only its *name*. The ValheimCLI Standard pack must advertise `valheim.session/direct-start`, and the runner verifies that this process consumed its one-use request. Native Valheim 1.0.16 runs passed for a local world, listen host and dedicated-server join, including exact world and character, grounded arrival, mod assertions, save, restart and cleanup. With the dedicated server ready before either launch, one paired run reached its first mod assertion in 31.61 seconds by direct start and 31.54 seconds through the menu. Direct start removes menu interaction; this run did not show a startup speedup. The vanilla `-joinserverwithcharacter` argument and whether a menu frame is ever visible have not been verified here.

The hosted MyMod example marks its copied fixture and staged character as cheated before issuing its cheat-gated mod commands. The dedicated `lifecycle-world` example does the same on its first prepared-character round. Valheim 1.0.16 asks for cheat acknowledgement even when devcommands are on, including for the example's read-only prefab census. Vanilla `confirmcheats` goes to the server when a client has joined one, leaving that client's character unmarked; the ValheimCLI Standard pack's `cli_acknowledge_local_cheats` explicitly marks the local disposable character and reports the result. These acknowledgements are restricted to owned, prepared disposable characters; they are never sent to an attached client. A site or load test that does not need cheat commands should leave the character unmarked. If the disposable station client's BepInEx log contains the known `Unable to start Unity log writer` header, a plan can list that **exact** line under `logScan.unknown-error.expected` with a written reason; other errors still fail, and the Unity Player.log is scanned separately.

This path passed a bounded [native Windows 1.0.16 joined-client check](https://github.com/tvongaza/ValheimTesting/pull/94#issuecomment-5912134571): prepared local copies started grounded at two dry points 1.9 km apart with no first-round teleport; copies whose saved point differed from the plan were refused before launch. The normal teleport flow also passed. The test does not establish other game versions or hosted, attached, or remote-profile clients. Use only a disposable local character, never a personal or Steam Cloud character, and rely on the client's support observation rather than the prepared file alone as arrival evidence.

Observations use ValheimCLI's generic commands (`cli_zdos_at` on the server, `cli_prefabs_at` on the client). A mod that needs a test-only action or observation adds it to its adapter as another extension command.

## Run the layers

```sh
# Unit and integration: no game.
dotnet test examples/ModWithTests/MyMod.Tests/MyMod.Tests.csproj -c Release
dotnet test examples/FullLifecycle/MyMod.IntegrationTests/MyMod.IntegrationTests.csproj -c Release

# Game-side projects, against your install and the ValheimCLI core in the test runtime:
dotnet build examples/FullLifecycle/MyMod/MyMod.csproj -c Release -p:ValheimPath="<install>"
dotnet build examples/FullLifecycle/MyMod.TestAdapter/MyMod.TestAdapter.csproj -c Release -p:ValheimPath="<install>" -p:CliDll="<runtime>/BepInEx/plugins/valheimCLI.dll"

# The native system test: check the plan and copy the fixtures without launching anything, then run.
dotnet run --project examples/FullLifecycle/MyMod.SystemTests -c Release -- validate plan.json <new-output-directory>
dotnet run --project examples/FullLifecycle/MyMod.SystemTests -c Release -- run plan.json <new-output-directory>
```

The runner prints `PASS` or `FAIL` and exits 0 only on a pass. After the client and server stop, it scans their logs (the server's per boot, the owned client's BepInEx log and `Player.log`) for known problems such as a Harmony patch on a method that does not exist; a failure pattern fails the run, and every count is in `result.json` ([log scan](../../docs/testing-toolkit.md#log-scan-at-teardown)). Every run writes `result.json` and `junit.xml` (every step, with its error), per-boot server logs, `connection-N.jsonl` and `client-commands.jsonl` (every command sent), `client-process.json` for an owned client, and the arrival observations.

## Prepare the native run

1. **A server runtime** with BepInEx, ValheimCLI (core and the Standard and WorldTools packs), the mod and the adapter; its ValheimCLI `[Server] Port` equals the plan's `port`. Keep it clean: BepInEx core plus these plugins, with an empty `BepInEx/patchers` folder (the runner refuses any patcher the plan's `patchers` does not name, and so does an owned client's launch for its install). Pin it by hash with `WorldFixture.Manifest`, and its game build, BepInEx core and patchers with `InstallPins.Of` in `runtimePins`; the runner works on a copy and refuses one whose game build or loader differs ([what each pin protects against](../../docs/testing-toolkit.md#pins-and-the-opt-out)).
2. **A world fixture** that has never been marked, pinned the same way. Pick the sites from its generator heights (for example with `cli_ground_height` in [GameObserve](../GameObserve/README.md)) and write them into the plan. They are your expectations: the runner never takes them from the mod.
3. **A client install** with BepInEx and ValheimCLI (core and the Standard pack) only: the mod is pinned `absent`, because the claim is that a client without it sees the marker. Its ValheimCLI port differs from the server's, and its ValheimCLI settings include `AllowOnServerClients = true`: Valheim 1.0 refuses a joined client's cheat commands whatever the server's admin list says, and this ValheimCLI opt-in is what lets its test commands (protection, object listing) run there. It needs an existing, disposable local character; never use a Steam Cloud character. For an owned client, pin the install's game build, BepInEx core and patchers with `InstallPins.Of` in `client.installPins`: Steam updates the game on its own, and the launch refuses an install that no longer matches.
4. **The plan**: start from [sample-plan.json](MyMod.SystemTests/sample-plan.json) and replace every path, hash and coordinate. This example runs with strict pins only: its scenario joins and checks by the pinned world uid, so it refuses `"pinning": "none"`.

## The server half alone

With `"scenario": "dry-site-server"` the runner runs only the server steps: no marker before, the mod marks the dry site and refuses the wet one (each asked once), the saved objects show it, confirmed save, restart of only the owned server, the marker is still there and the wet site still empty. The plan has no `client`, `review` or `arrival` section; a client section is refused, because nothing here looks from a client. The result says only that the mod and its adapter load on that server and the marker persists; it says nothing about what a client sees.

Nobody needs to prepare a fixture for it by hand:

```sh
dotnet run --project examples/FullLifecycle/MyMod.SystemTests -c Release -- prepare-server <server-runtime> <new-output-directory>
dotnet run --project examples/FullLifecycle/MyMod.SystemTests -c Release -- run <that-directory>/plan.json <another-new-output-directory>
```

`prepare-server` takes a server runtime with BepInEx, ValheimCLI (core, Standard, WorldTools; `[Server] Port = 5577`), the mod and the adapter and nothing else in `BepInEx/plugins`. It pins the runtime by hash and those five plugins by MD5, starts one owned server on a copy (the game creates a new world; this boot accepts any world, the plugins stay strictly pinned), reads the world uid and picks the sites from the world generator's heights through ValheimCLI's `valheim.world/terrain-grid`, never from the mod: the dry site at least 3 m above the rule's 31.5 m, the wet one at least 3 m below, the closest such samples to the world's centre, 50 m apart. It confirms a save, stops its server and writes `plan.json`. It prints `PREPARED`, never `PASS`: preparing is not the test. The plan holds a generated throwaway server password; share the reports, not the plan.

The repository's [scheduled server checks](../../docker/linux-server/README.md#scheduled-checks-in-ci) run exactly these two commands in the Linux server image every night.

## Owned or attached client

- **`owned`**: the runner launches the client from `client.install` and stops that process, and only that process, when the scenario ends, whether it passed or failed. It refuses before launching if something already listens on the client's CLI port, if no Steam client is running, or if the join password variable is missing from its own environment (the client inherits it). The runner must run **in the desktop session where Steam is running and signed in**, with a display: a client started from a service, a scheduled task without a desktop, or a plain SSH session cannot open a window or reach Steam. On an Apple Silicon Mac the client runs under Rosetta unless the client section sets `"architecture": "arm64"`, which launches it natively from an install with the native BepInEx stack ([native client](../../docs/getting-started.md#native-apple-silicon-client)).
- **`attach`**: you launch the client and sign in yourself, then run the runner; it connects to the client's ValheimCLI port, leaves the world at the end and never touches the process. Use it when the runner cannot run in the client's desktop session, or when you want to watch or keep the client.

Either way the server is always owned: the runner copies the fixtures, starts that copy, proves by the adapter's identity handshake that it is talking to the process it started, and stops only that process.

## The rounds

The client half of the scenario is the toolkit's `ClientRounds` (see [Plan rules and client rounds](../../docs/testing-toolkit.md#plan-rules-and-client-rounds-preview-13)). `DrySiteScenario` supplies only what is this mod's:
- the arrival point and the name of its step (`arrive beside the marker`);
- the measurement: the client sees the marker at the dry site, and in the last round the optional human review;
- the check after the restart: the server still has one marker at the dry site and none at the wet site.

The helper waits until the server accepts connections, then joins, protects and arrives, and runs the measurement. Between the two rounds (`first`, `after-restart`) it saves with confirmation, has the client leave and restarts only the owned server; after the last round the client leaves. It closes the client in every outcome. Each round's steps in `result.json` start with the round's name, and its evidence files do too (`first-arrival.json`, `after-restart-arrival.json`). `LifecyclePlan` uses the toolkit's plan rules (`RequireScenario`) for the generic checks and keeps its own for the sites.

## Cleanup and its limits

On any failure, including a failed step, an exception or Ctrl+C, the runner still stops the owned client, then the owned server, and writes the report. The copies stay in the output directory for inspection; the pinned sources are never changed.

What the runner does not and cannot clean up:

- **Steam**: it never signs in, out or switches accounts, and it cannot tell whether Steam is signed in, only whether it runs. Log-in and Steam Guard are yours; use an account you can dedicate to testing.
- **The machine**: it never stops a process it did not start, never finds processes by name, and never edits the client install's settings. Your character's save changes as it would in play (the first spawn, the position). Claim the machine for yourself while a run is going: two runs sharing one install, one port or one Steam account interfere.
- **An attached client** keeps running, in its menu.

## Human review

With `"review": { "enabled": true, "seconds": 600 }`, after the automated checks of the second round the runner leaves the player beside the marker, prints `REVIEW:` with what to look at, and waits for you to write `review.json` in the output directory: `{"verdict":"pass","notes":"..."}`. The verdict is recorded in `result.json` as `humanReview`; it never adds a step, so the automated result cannot change because of it, and a missing verdict is recorded as such. Walking and appearance are for people to judge; the runner judges only what it measured.

### Repeatable review stills

[`sample-plan-review-capture.json`](MyMod.SystemTests/sample-plan-review-capture.json) shows a separate `review-capture` run. It starts one pinned dedicated server and one owned, joined client, protects the disposable character, arrives at the declared dry point, and captures two stills with the same declared weather, time, camera distance, height, azimuth, mist and clutter settings. Change `cameraAzimuthDegrees` if a tree blocks the subject; the two images in a single run always use the same value. It writes `review-first/first.png` and `review-second/second.png` with JSON sidecars recording those conditions, the world UID, game build, plugin pins and each image's SHA-256. `ReviewCapture.CaptureAsync` restores the client's prior safety, cheats, weather, time, mist, clutter and camera state after each image, including when capture or transfer fails. The adapter also attempts restoration on unload.

Run it with the normal pinned runner after replacing the sample's paths and hashes:

```sh
dotnet run --project examples/FullLifecycle/MyMod.SystemTests -- validate review-plan.json new-review-output
dotnet run --project examples/FullLifecycle/MyMod.SystemTests -- run review-plan.json new-review-output
```

The client must run on the runner's machine for this example. For a remote client, use its `IGameHost` with `ReviewCapture.CaptureAsync` in your own scenario. The capture is **evidence for human review**, never an assertion that the scene looks correct. Review the PNGs yourself and record a verdict separately. Use a fresh output directory and an empty no-space host capture directory for each run. The example keeps the original host PNGs under `capture-host-*` beside the fetched copies for audit; remove the run directory when you have archived the evidence. A failed or interrupted run can leave a host image, but cannot turn it into a passing review result.

### Bounded motion evidence

`ReviewClip.CaptureAsync` records a short, opt-in scene-only sequence from a ready, strictly pinned client. The adapter renders a second world camera into a private off-screen texture; it does not record the desktop, account screens, chat or screen-space UI. UI layers and layers used by world-space canvases are excluded, so a mod that places ordinary scenery on a UI layer may leave it out of the clip. Check an actual frame before using the result as evidence. The native 1.0.16 check captured 15 frames at 320×180 and 5 fps in a disposable local world; the inspected frames showed the world without the screen UI. It does not establish that every mod's custom UI is excluded.

The game-side frame source is opt-in: define `VALHEIM_TESTING_REVIEW_CLIP` in the adapter project and reference the game's `UnityEngine.UIModule` and `UnityEngine.ImageConversionModule`, as this example does. Other adapters compiling the source package do not gain new Unity references. After the client is in the intended world and its player is ready, with ValheimCLI devcommands enabled, register `ReviewClipFrames.Command()` beside `ReviewState.BeginCommand()` and `ReviewState.RestoreCommand()`. Pass its owned client host and the same exact world and plugin pins used for the run:

```csharp
var plan = new ReviewClipPlan(
    Id: "approach-01", ExtensionId: "mymod.testing",
    HostDirectory: @"C:\test-runs\my-run\capture", EvidenceDirectory: evidencePath,
    WorldUid: expectedWorldUid, GameBuild: expectedGameBuild,
    PluginPins: expectedPluginPins, Width: 320, Height: 180,
    FramesPerSecond: 5, Frames: 15);
ReviewClipReceipt clip = await ReviewClip.CaptureAsync(client, clientHost, plan,
    fetchTimeout: TimeSpan.FromSeconds(30), cancellation);
```

The result is a local `.apng` that plays once, its source PNG frames and timestamps, and a JSON sidecar with actual duration and frame rate, world/build/plugin pins and SHA-256. Each call needs a fresh host directory and local evidence directory; the 10-second, 60-frame, 640×360 and 24 MiB ceilings reject unbounded requests. A failed capture or transfer leaves no passing local evidence. Capture uses a separate, strictly pinned ValheimCLI connection so cancellation can close an in-flight request while retaining the original connection for review-state restoration. The game-side cancellation behavior requires the maintained ValheimCLI fork containing the disconnect-abandon fix from [ValheimCLI PR #40](https://github.com/jneb802/valheimCLI/pull/40); an older client may continue capturing after the host cancels. The adapter restores its review state on unload too. The host's owned run directory may retain complete source frames for audit; remove it with the rest of that disposable run. The sidecar starts with `visualVerdict: "not asserted"`; watch the clip and record the human verdict separately. Animated PNG can be opened in a browser that supports APNG, and every source frame remains a normal PNG for inspection.

## Native campaign

### Read-only objects at a joined site

[`sample-plan-area-objects.json`](MyMod.SystemTests/sample-plan-area-objects.json) shows the separate `area-objects` run for issue #201. It joins an owned, pinned client to a disposable dedicated server, arrives at dry ground, then calls `AreaObjectSnapshot.Capture` at a 16 m radius. The server supplies saved ZDO and container rows; the client supplies loaded prefabs and current piece-support rows. The runner writes the complete replies to `area-object-snapshots/site-001.json` and links its SHA-256 from `result.json`. This is optional diagnostic evidence: it checks complete replies and world identity, not whether MyMod should have placed a particular object. Use the normal `validate` and `run` commands with the sample's paths and hashes replaced. See [object snapshots](../../docs/area-object-snapshots.md) for the contract and privacy limits.

Six more scenarios, and a hosted run, take the toolkit's lifecycle steps and world observations through one native campaign: a real dedicated server owned by the runner (as above, or on another host with `--profile`) and a real Windows client. Each plan runs one scenario; together they prove the native acceptance items of issues #20, #23, #24, #26, #30, #31, #32, #33, #34 and #35 that a game can prove, and #91's content census once its native run is done (below). The dry-site feature is unchanged. What the campaign adds is small and kept apart:

- **MyMod** gains a version handshake adapted from the wiki's [RPC version-handshaking concept](https://github.com/Valheim-Modding/Wiki/wiki/RPC-Version-Handshaking) ([VersionHandshake.cs](MyMod/VersionHandshake.cs); its net version is a build property, so `-p:MyModNetVersion=2` builds a mismatched MyMod), one server-synced config entry with its own routed-RPC sync ([SyncedGreeting.cs](MyMod/SyncedGreeting.cs), `[Server] Greeting`, changed on the server with `mymod_greeting <word>`), a custom-data key on the player (`mymod_note <word>` writes `mymod.note`) and a saved label on its marker (`mymod_label`), and registered content for the content census ([Content.cs](MyMod/Content.cs): one item, `MyMod_SurveyStake`, registered in ObjectDB and as a ZNetScene prefab, one recipe, `Recipe_MyMod_SurveyStake`, a Hammer-table piece `MyMod_SurveyPost`, and a status effect `MyMod_SurveyBlessing`; `-p:MyModOmit=recipe` and `-p:MyModOmit=status-effect` build two separate census controls). Unlike the wiki example, a client without MyMod still joins; only an installed, mismatched copy is refused. Nothing spawns the item. The wiki pattern predates 1.0; the native checks below are the evidence for this example's behavior on 1.0.16.
- **MyMod.TestAdapter** registers the toolkit's adapter commands (`zones`, `custom-data`, `globalkeys`, `globalkey` behind the `MYMOD_TEST_FIXTURES` gate, `config`, `unresolved-prefabs`, `dungeon-rooms`, `harmony`, `content-census`) and the mod's own marker and ownership commands. It depends on MyMod only softly and never on its types, so it also loads on a client without MyMod.
- **[Controls](Controls/)**: four tiny plugins, each a deliberate defect for one check, and two defective MyMod builds (`omitted-recipe`, `omitted-status-effect`). They are never in a normal runtime: a plan that pins a control plugin is refused unless it names it in `expectFailure`, and then the run passes only if that check fails for the control's named reason (a control whose defect the check cannot see fails the run). The run ends after the expected failure. The plan cannot tell the omitted-recipe build from the normal one by its pins; only the census's failure on that recipe alone shows which one ran.

| Sample plan | Scenario | Server runs | Client runs | Proves |
|---|---|---|---|---|
| [lifecycle-world](MyMod.SystemTests/sample-plan-lifecycle-world.json) | `lifecycle-world` | MyMod, adapter; `environment.MYMOD_TEST_FIXTURES=1` | MyMod, adapter | #30 census of MyMod's patches; #23 a boss key set once on the server reaches the client and survives the save and restart; #24 a vanilla dungeon's saved rooms lie in its location's zone, and its interior offset; #35 the zone cycle (the client unloads the marker's zone and loads it again, the marker's saved label is back) and the logout cycle (the character file is rewritten, this run's `mymod.note` comes back) |
| [vanilla-client](MyMod.SystemTests/sample-plan-vanilla-client.json) | `vanilla-client` | MyMod, adapter | adapter only, MyMod pinned `absent` | #33 a client without MyMod beside its objects resolves every prefab hash and logs no missing prefab, missing RPC handler or unload error, in both rounds |
| [synced-config](MyMod.SystemTests/sample-plan-synced-config.json) | `synced-config` | MyMod, adapter | MyMod, adapter | #20 both sides hold the same greeting after the join; an admin change reaches the client within the wait (woken by MyMod's "received" line in the owned client's live log); after a restart the client gets the server's saved value again |
| [refused-join](MyMod.SystemTests/sample-plan-refused-join.json) | `refused-join` | MyMod (net version 1), adapter | first `refusedClient`: MyMod built with `-p:MyModNetVersion=2`; then `client`: the server's MyMod and adapter | #34 the mismatched client is refused with `expectedRefusal` (default `ErrorVersion`, 3) read back at its menu, and the server then keeps a matching client |
| [crossplay](MyMod.SystemTests/sample-plan-crossplay.json) | `crossplay` | MyMod, adapter; `"crossplay": true`, an explicit `-port`, no `-password` | MyMod `absent`, `"crossplay": true` | #32 the dry-site lifecycle joined through each boot's PlayFab lobby; the report records `crossplay` and `clientJoin` |
| [hosted](MyMod.SystemTests/sample-plan-hosted.json) | `hosted` (its own mode) | none | MyMod, adapter, `hostWorld` | #31 MyMod's feature on a host (mark, refuse, saved objects, persistence across the host's restart) and MyMod's broadcast handler running in the host's own process |
| [content-census](MyMod.SystemTests/sample-plan-content-census.json) | `content-census` | MyMod, adapter | MyMod, adapter (the server's builds) | #91/#114 in each round (first, after restart and rejoin) each side observes MyMod's declared item, prefabs, recipe, Hammer-table piece and status effect ([content-expectations.json](MyMod.SystemTests/content-expectations.json)); dependencies resolve, no undeclared content appears in scope, and the observation comes from that side's pinned MyMod build. The #114 additions passed a native run. |
| [ownership-handoff](MyMod.SystemTests/sample-plan-ownership-handoff.json) | `ownership-handoff` | MyMod, adapter | Two simultaneous owned clients, each with MyMod and adapter on a different host and Steam account | #211 A explicitly claims the marker; B sees A's owner; A leaves; B explicitly claims it; server and B agree on one owner. This tests orchestration, not automatic ownership of every Valheim object. Native Windows dedicated server + macOS/Windows clients passed 47/47 steps (Valheim 1.0.16) |

For `ownership-handoff`, pass `--profile` to the runner. The private environment profile names `client-a` and `client-b` on different hosts, gives each a different `steamAccount` from the pool, and maps their `cliPort` values to the sample plan's `client.port` and `secondClient.port`. Each host has its own game install and disposable local character. The relevant part of the profile looks like this (see [account leases](../../docs/testing-toolkit.md#a-runs-client-accounts-from-an-environment-profile) for the full host and pool format):

```json
{
  "clients": {
    "client-a": { "host": "gaming-pc", "install": "<client A install>", "runtime": "<client A run directory>", "cliPort": 5556, "steamAccount": "test_account_a" },
    "client-b": { "host": "second-host", "install": "<client B install>", "runtime": "<client B run directory>", "cliPort": 5557, "steamAccount": "test_account_b" }
  },
  "steamAccounts": { "pool": "steam-accounts.json", "leaseHost": "lease-box", "checkSignedIn": true }
}
```

Run `validate` first: a missing profile, shared account or host, ambiguous port or character, missing capability, or wrong plugin pin is refused before a fixture copy or launch. Then run the scenario with the same plan and profile. The profile stays private; the [sample plan](MyMod.SystemTests/sample-plan-ownership-handoff.json) uses placeholder file hashes and a documentation-only server address. The named joins are game-side waits: ValheimCLI returns after each player is ready, and the runner makes one world-identity check rather than repeatedly querying state.

Give the clients separate landing points at least 3 m apart, each beside the marker rather than on it. Before either client launches, the server checks the declared heights against the fixture's generator and refuses wet targets. After each teleport's floor-ready signal, the client measures loaded terrain at its target, which may include location levelling absent from the raw generator. That measured height is a placement input, not a terrain-correctness assertion; a separate player-support observation must still confirm proximity, grounding and low speed. Failed arrival keeps a bounded read-only support/ground diagnostic before teardown.

The runner issues each join, local teleport and ownership claim once. Both clients require `eventDrivenArrival: true`; the runner refuses `fastTestTeleports` because joined-client cold hops have not passed the [timing check](https://github.com/tvongaza/ValheimTesting/issues/216). ValheimCLI's game-side teleport waits and the adapter's marker-owner notification wait complete those transitions; a lost action reply is not retried. The adapter records the marker's ZDO owner and the observing process's ZDO session ID, so the server and B can agree on the same owner rather than infer it from peer order. The ownership hook is test-only and belongs in this example adapter; shared doubles do not model Valheim's ownership rules. A listen-server version needs a separate host plan because one host is also a player; this example covers a dedicated server. Before a native run, verify the required Standard CLI commands in the pinned build too; [#217](https://github.com/tvongaza/ValheimTesting/issues/217) tracks making that preflight automatic.

| Control ([Controls](Controls/)) | Install on | `expectFailure` in | Check that must fail, and the reason it must name |
|---|---|---|---|
| [MissingHarmonyTarget](Controls/MissingHarmonyTarget/Plugin.cs) (`example.mymod.control.missingtarget`) | server | lifecycle-world | #30 the census names its missing patch (`not applied: postfix (any method) on Player::MyModControlMethodThatDoesNotExist`), and #26 the log scan's defaults fail on the server's live BepInEx log with `accesstools-not-found` naming the method. On the Valheim 1.0.16 Windows dedicated server (BepInEx 5.4.23.5, HarmonyX 2.9.0) that warning is all BepInEx's log has; `PatchAll` then throws `Undefined target method`, which only Unity's log (`-logFile`) records and the teardown scan counts as `harmony-undefined-target`. The control logs `MissingHarmonyTarget: PatchAll returned` right after `PatchAll`, and the run records whether that line is there (`controlPatchAllReturned`; natively `false`) without failing on it. Its plan names both lines as expected (below) |
| [ServerOnlyPrefab](Controls/ServerOnlyPrefab/Plugin.cs) (`example.mymod.control.serveronlyprefab`) | server | vanilla-client | #33 the runner spawns its object beside the dry site (`mymodcontrol_spawn`), and the vanilla client's census names its hash and `MyModControl_ServerOnly` |
| [FieldOnlyState](Controls/FieldOnlyState/Plugin.cs) (`example.mymod.control.fieldonlystate`) | client | lifecycle-world | #35 a value kept only in a component field on the marker `did not survive the zone reload`, while MyMod's saved label did |
| [SuppressedProfileSave](Controls/SuppressedProfileSave/Plugin.cs) (`example.mymod.control.suppressedsave`) | client | lifecycle-world | #35 the logout check times out waiting for the character file `to be rewritten by the logout` |
| `omitted-recipe`: MyMod built with `-p:MyModOmit=recipe` (a build, not a plugin) | server and client, pinned as `example.mymod` | content-census | #91 `the census fails only on the omitted recipe`: `Recipe_MyMod_SurveyStake` missing on the server and on the client, and nothing else wrong; any other failure, or a census that passes, fails the run |
| `omitted-status-effect`: MyMod built with `-p:MyModOmit=status-effect` | server and client, pinned as `example.mymod` | content-census | #114 `the census fails only on the omitted status effect`: `MyMod_SurveyBlessing` missing on both sides, with no unrelated failure |

A control run is the scenario's own plan plus the control's pin (by MD5, on the side the table says) and `"expectFailure": "<name>"`. Both of MissingHarmonyTarget's lines fail the teardown log scan by default, so its plan also names them as expected, and the scan counts them apart instead of failing the run a second time on the defect the control's own check already required: `"logScan": { "accesstools-not-found": { "expected": ["MyModControlMethodThatDoesNotExist"], "reason": "..." }, "harmony-undefined-target": { "expected": ["MissingHarmonyTarget.Plugin+PatchMissingMethod"], "reason": "..." } }`. The plan is refused without them.

**Probes** ([Probes](Probes/)) plant no defect and need no `expectFailure`; pin them like any plugin. [QuitLog](Probes/QuitLog/Plugin.cs) (`example.mymod.probe.quitlog`) logs a warning in `OnApplicationQuit` and one in `OnDestroy`, so a run shows its teardown log scan reading what a mod logs while the game quits (#85): the scan counts them as `unknown-warning`, the first as its first line. BepInEx 5.4 flushes its disk log every 2 seconds and not again at quit, so a boot's last lines can be missing from the kept log even after a clean stop ([log scan](../../docs/testing-toolkit.md#log-scan-at-teardown)); the adapter therefore enables the toolkit's `QuitLogFlush`, and a plan's `environment.MYMOD_TEST_NO_QUIT_FLUSH=1` turns it off as the negative control.

**Adapter unload (#30).** A dry-site-server plan may add a `patchReload` section: after the restart, the runner copies the [PatchReload](Probes/PatchReload/Plugin.cs) probe (`example.mymod.probe.patchreload`) into ScriptEngine's `BepInEx/scripts`, replaces it with a second build, then deletes it, and after each change reads the whole Harmony census. The probe patches `Terminal::InitTerminal`, the method MyMod patches, under its own ID and unpatches only that ID when unloaded (`UnpatchSelf`), as an adapter should; every other owner's patches must be exactly as before, and MyMod's declared patch applied. Each change waits for ScriptEngine's own "reloaded" line before it reads the pins: the pins hash the file, which changes before the reload, and while the reload runs the probe is briefly not loaded. The runtime needs ScriptEngine in its plugins, pinned, with `LoadOnStart` and `EnableFileSystemWatcher` on and an empty `BepInEx/scripts`; the probe is not pinned (the scenario pins each build as it loads). Build the probe twice (`-p:ProbeRevision=B` for the second) and name both:

```json
"patchReload": { "revisionA": "<full path>/A/MyMod.Probe.PatchReload.dll", "revisionB": "<full path>/B/MyMod.Probe.PatchReload.dll", "reloadSeconds": 60 }
```

The control is the `-p:ProbeUnpatch=Other` build as `revisionA` with `"expectOthersRemoved": true`: its unload also removes MyMod's patches (`Harmony.UnpatchID("example.mymod")`), and the check after the reload must fail naming them. Not `Harmony.UnpatchAll()`: that removes ValheimCLI's patches too, and no CLI command ran after it, so the run lost the census it reads; the teardown scan's `harmony-unpatch-all` failure is what names that case. Local runs only (no `--profile`): the scenario writes into the runtime copy. Each census is kept as `patch-reload-{stage}.txt`.

The integration tests ([CampaignScenarioTests](MyMod.IntegrationTests/CampaignScenarioTests.cs), [HostedScenarioTests](MyMod.IntegrationTests/HostedScenarioTests.cs), [CampaignPlanTests](MyMod.IntegrationTests/CampaignPlanTests.cs)) run every scenario, each control's expected failure, each control whose check would pass, and every plan refusal against scripted replies; they read the sample plans too.

### Prepare the campaign

For a short server plus two-client setup smoke, use the runnable
[`ThreeActorSmokeScenario.cs`](MyMod.SystemTests/ThreeActorSmokeScenario.cs),
[`sample-plan-three-actor-smoke.json`](MyMod.SystemTests/sample-plan-three-actor-smoke.json) and
[`sample-three-actor-campaign.json`](MyMod.SystemTests/sample-three-actor-campaign.json). The scenario owns one
dedicated server and two clients, joins both clients to the same pinned world, verifies their plugins and world identity,
then checks that the server sees two peers. Its named checkpoints require both clients at their menus, both joined,
client B back at its menu while A stays in the world, and both joined again. Each transition is requested once and
the next checkpoint reads the resulting state; a failed or uncertain transition is not retried. This tests setup and
rejoin, not marker ownership or gameplay. The underlying
`HostedCampaignPreparation` accepts any number of named clients beside one dedicated server: each role is a separate
profile/lock/character object, and `ApplyTo` binds a dictionary of client plans by name. The example binds only
`client-a` and `client-b` because its assertion expects two peers. A different mod can bind three or more without
changing preparation. Simultaneous clients need different Steam account leases, hosts and registered character player IDs.

The server and every client prepare their runtime in parallel, with one claim per host. After the server's ready
checkpoint, `OpenProfileClientsParallel` launches the named clients concurrently and waits for all of them at the
menu checkpoint. The join and rejoin checkpoints are explicit so a test can pause one actor while the others stay in
the world. A failed client start closes the other successful starts before teardown; it does not advance the test.

Copy the two sample JSON files to a private test directory. In the campaign manifest, name the private environment
profile, fixture world, direct-join address, and a reviewed dependency lock for each role. Create each lock from an
explicit `NativeDependencyRequest` with `dotnet run scripts/native-dependencies.cs -- resolve request.json lock.json`;
the lock selects the mod, its hard dependencies and one coherent ValheimCLI core and packs. The server lock must include
WorldTools because the two-peer checkpoint calls `cli_peers`; `campaign check` refuses a lock without it before the
game starts. If the source install's loader is unsuitable, set `loaderPackage` on that campaign role to a reviewed
`BepInExLoaderPackage` manifest. The existing package API captures and validates its files and hashes; campaign preparation
applies them only to the disposable copy. Do not repair the live source install or copy individual Doorstop files by hand.
A nested `BepInEx/core/core` can make the preloader load Harmony twice and abort before writing its main log.

Campaign clients require `steamAccounts.checkSignedIn: true` and distinct Steam IDs in their private pool. Preparation
matches each host's account before copying; launch repeats that check under the account lease, then verifies the game's
own identity. Unix's remembered Steam login alone is not proof of the running game's account. Identity values are redacted
from the recorded identity reply. The FullLifecycle runner opts into `TestAccess.Ensure`: dedicated servers acknowledge
cheats locally, and clients acknowledge their disposable character after joining. `AllowOnServerClients` must already be
set in the staged configuration for client mutations; the helper never grants it at runtime. Register two clean, distinct
test characters with `DisposableCharacterStore`; one seed copied twice is still one player. Set the server password and
client password variable in the private plan/environment as for any owned-server run. The sample paths and password are
placeholders, never defaults that the runner guesses.

From the repository root, these are the preparation check and the complete run:

```sh
bash scripts/run.sh campaign check /private/test/campaign.json /private/test/three-actor-plan.json
bash scripts/run.sh campaign run /private/test/campaign.json /private/test/three-actor-plan.json /private/test/runs/first
```

`check` reviews local locks, characters, fixture metadata and the host/account profile without touching a host. `run`
starts with the repository launcher's NuGet cache write check, so an unwritable user cache is replaced before .NET
tries to restore packages. On Windows the same entry point is `./scripts/run.ps1 campaign ...`. The run then
takes one lock per host, checks for conflicting client or owned-runtime processes, and prepares all named actors concurrently, even when a server and
client share a host. Each actor gets its own clean install copy, selected mod and ValheimCLI files, and the clients get
separate registered character names. The runner waits for every preparation to settle before it starts the server;
on failure it retires every copy whose ownership was established. It then derives strict pins and calls the usual
`PinnedServerRun`. The campaign also copies the manifest's one-world fixture into the dedicated server's
`worlds_local` layout. The original fixture stays read-only; this layout matters because Valheim silently creates a
new world when a world of the requested name is absent from `-savedir/worlds_local`.
It attempts to retire the prepared copies after the runner stops. If a process is still active or a stop cannot be
established, the guarded cleanup refuses to remove its character or install and names what remains for inspection.
The output holds the generated plan, role-specific CLI manifests, normal `result.json` and JUnit evidence, plus
`campaign-times.json` with preparation, scenario and cleanup seconds. No game
startup is part of the `check` result.

1. **Builds.** Build MyMod twice: normally, and with `-p:MyModNetVersion=2` into another folder (`-o`), for the refused client; for the two content-census controls, build separate copies with `-p:MyModOmit=recipe` and `-p:MyModOmit=status-effect`. Build the adapter and the controls you run (FieldOnlyState needs `-p:CliDll=`, as the adapter does). Pin every DLL by MD5 in the plans.
2. **The server runtime** as in [Prepare the native run](#prepare-the-native-run), with ValheimCLI's core, Standard and WorldTools packs, MyMod and the adapter; pin all five in `pins`. A control run adds the control plugin to `BepInEx/plugins` and to `pins`.
3. **A fixture world** that has never been marked, with a vanilla dungeon near the sites: `dungeon` is a dungeon location's ground position within two zones (128 m in x or z) of the arrival point, so the server generates its rooms while the player stands there (the step waits up to the client's `arrivalSeconds`). To find one, turn devcommands on on a server with the fixture world and run ValheimCLI's `cli_world_dump`; its locations list names each location with its position. Pick a dungeon location, such as `Crypt2` to `Crypt4` or `TrollCave02` (Black Forest) or `SunkenCrypt4` (Swamp), with dry ground (at least 31.5 m) for the dry site and arrival point beside it, and wet ground elsewhere for the wet site. `away` is dry ground at least 5 zones (320 m in x or z) from the dry site, where the player goes so the client unloads the site (more if the client's simulation distance is larger than the default; the run reads it and refuses before moving). `globalKey` is a key the world does not have yet (`defeated_eikthyr` for a fresh world).
4. **Client installs**, each with BepInEx and ValheimCLI's core, Standard and WorldTools packs (`AllowOnServerClients = true`), plus what the table says. One install can serve several plans if you change its plugins between runs; `refused-join` needs two installs, one per MyMod build, run one after the other. Pin each install's game build, BepInEx core and patchers in `installPins`.
5. **A disposable local character** (`characters_local`, never Steam Cloud), existing and used only for tests; `logout.charactersDirectory` is that folder's full path. The logout scenario rewrites it, and the SuppressedProfileSave control keeps it from being saved at all.
6. **Crossplay.** A separate fixture server, private (`-public 0`) and without `-password` (the crossplay join cannot send one), on a game port (`-port`) that no other server behind the same public IP uses: a crossplay lobby is keyed by public IP and port, and a second server on that port takes the first one's joins. Do not switch one fixture between Steam and crossplay.
7. **Hosted.** A small fixture world for the host, pinned in `hostWorld`: Valheim 1.0's chunked save (one `<name>/` directory) or the older `<name>.fwl` and `<name>.db`; the runner places a copy in the client's `worlds_local` (refusing a name already there) and moves it into the evidence afterwards.

Start each plan from its sample and replace every `<...>`: paths, SHA256 manifests (`WorldFixture.Manifest`), `InstallPins.Of`, MD5s and the world UID. Validate first; nothing launches:

```sh
dotnet run --project examples/FullLifecycle/MyMod.SystemTests -c Release -- validate lifecycle-world.json <new-output-directory>
dotnet run --project examples/FullLifecycle/MyMod.SystemTests -c Release -- run lifecycle-world.json <new-output-directory>
# The hosted scenario has no dedicated server; it has its own two modes.
dotnet run --project examples/FullLifecycle/MyMod.SystemTests -c Release -- validate-host hosted.json <new-output-directory>
dotnet run --project examples/FullLifecycle/MyMod.SystemTests -c Release -- host hosted.json <new-output-directory>
```

### Evidence

Every run writes `result.json` and `junit.xml` with each step, as above. The scenarios add, per round: `{round}-arrival.json`; `first-global-keys.json`, `first-dungeon-rooms.json` (rooms, location, interior offset; also `dungeonInteriorOffset` in the provenance), `first-zone-cycle.json`, `after-restart-logout.json`; `{round}-vanilla-client-1.json`, `{round}-vanilla-client-logs.json`; `first-greeting-joined.json`, `first-greeting-changed.json`, `after-restart-greeting-after-restart.json`. `refused-join` keeps each client's command record and logs in `refused-client/` and `matching-client/`, and its provenance has `refusal`. A control run records `expectFailure`, `controlFailure` (the expected failure's message) and `control`; the MissingHarmonyTarget run writes `control-log-scan.json` (the scan of the server's live log with the default classifications), `controlScanFailure` (that scan's failure), `controlLogLine` (the `accesstools-not-found` line naming the control's method, found among every such line, since other plugins log them too) and `controlPatchAllReturned` (`true` when the control's line after `PatchAll` is in the log, so `PatchAll` did not throw; `false` when it is absent, so it threw), the FieldOnlyState run `first-field-only-state.json`. The content-census scenario writes `{round}-content-census.json` (the expectations, each side's census as it reported itself, and the report) and records `contentExpectations` in the provenance.

### Native results

The campaign ran on 30 September 2026 against Valheim 1.0.16 (BepInEx 5.4.23.5):

- **lifecycle-world** passed 47 of 47 steps: MyMod's Harmony census; the zone cycle (the client unloaded the marker's zones and loaded them again, and the marker came back with its saved label); the logout cycle (the character file was rewritten and this run's note came back after rejoining); `defeated_eikthyr` set on the server, listed on the client and kept across the save and restart; a `SunkenCrypt4`'s saved rooms (1.0 `roomData`) all in its location's zone, with an interior offset of about (0.63, 5001, 0.26).
- **Each control** failed its check for the named reason: the census named MissingHarmonyTarget's missing patch; FieldOnlyState's field-only value was lost on the zone reload while MyMod's saved label came back; with SuppressedProfileSave the logout check found the character file unchanged; the vanilla client's census named ServerOnlyPrefab's object by its hash, `1334784479` (`MyModControl_ServerOnly`). The `controlPatchAllReturned` record was added after that run.
- **A second pass the same day** (clean stops, the teardown scan's new defaults): lifecycle-world passed 47 of 47 again and gave the clean-run counts in the [log scan](../../docs/testing-toolkit.md#log-scan-at-teardown) docs. MissingHarmonyTarget passed 13 of 13: the census named the missing patch, the default scan of the server's BepInEx log failed on `accesstools-not-found` naming the method, `controlPatchAllReturned` was `false` (`PatchAll` threw; its `Undefined target method` error was in Unity's log only), and the teardown scan counted both named lines as expected. Without the Unity log's line named, the same run's teardown scan failed on it. The server half with the QuitLog probe passed 17 of 17: one boot's kept BepInEx log had both QuitLog warnings (the scan's first `unknown-warning`); the other boot's had neither, although it quit cleanly, which is BepInEx's disk log flushing every 2 seconds.
- **synced-config** passed (the server's change reached the client, and the client got the saved value again after a restart); **vanilla-client** passed (a client without MyMod resolved every prefab hash near the site); **refused-join** passed (the mismatched client was refused with `ErrorVersion` (3), and a matching client then joined).
- **Adapter unload (#30)**, dry-site-server with `patchReload` (ScriptEngine 11.1 on the 1.0.16 Windows server): 24 of 24 steps. The probe's postfix sat beside MyMod's on `Terminal::InitTerminal`, was replaced by the second build and then removed, and every other owner's patches (MyMod's four and ValheimCLI's) were unchanged after each change. The control (`-p:ProbeUnpatch=Other`) failed as named: `removed: example.mymod postfix MyMod.Plugin+RegisterCommands::Postfix() on Terminal::InitTerminal()` and MyMod's three other patches. An earlier control with `Harmony.UnpatchAll()` took ValheimCLI's patches with it and no CLI command ran afterwards.
- **Quit-time log lines (#99)**, dry-site-server with the QuitLog probe: with the adapter's `QuitLogFlush`, every boot's kept BepInEx log had both QuitLog warnings and ValheimCLI's `Command server stopped` (14 server boots over seven runs, and the client of a dry-site-lifecycle run after its clean stop), each with `Quit log flush armed by MyMod.TestAdapter OnApplicationQuit.`; with `MYMOD_TEST_NO_QUIT_FLUSH=1`, one boot of six lost its last two lines although it quit cleanly.
- **hosted** passed 17 of 17 steps, with the fixture in Valheim 1.0's chunked layout.
- **crossplay** passed 39 of 39 steps with the server on a remote Linux host through `--profile` and the Windows client started in its desktop session as a profile client: the lobby opened and the client joined through PlayFab, before and after a server restart.

- **content-census (#91/#114)**: the new piece/status-effect slice and its omitted-effect control pass controlled integration tests but await a joined-client native run. See [Registered content census](../../docs/testing-toolkit.md#registered-content-census-adapter-preview-4-game-preview-25).

### Limits

The in-run log reads (the vanilla client's log scan, the synced-config wake-up, the MissingHarmonyTarget scan, the hosted broadcast line) read live files on the runner's machine: an attached client's are its operator's, so those parts are skipped and the provenance says so, and a run with `--profile` skips the server's. The crossplay lobby is the exception: with `--profile` it is read in the server host's own log. Not covered: a second client joining a host (#31), an oversized room in a custom test dungeon (#24's negative control; the check is covered with scripted data), overlapping terrain edits across a restart (#24). The game-side projects (MyMod, the adapter and the controls) compile only against a game install, never in this repository's CI; CI runs the scenarios and plans against scripted replies.
