# Native acceptance suite

**Audience:** maintainers of this repository. **Concept:** the toolkit's own native acceptance scenarios, controls and probes, run in the real game on the suite's own mod, AcceptanceMod.

A mod author copies the [FullLifecycle example](../../examples/FullLifecycle/README.md): one small mod, one scenario, every test layer. This suite is the toolkit's: each scenario proves a few of the toolkit's native acceptance items with AcceptanceMod as the mod under test, and each control plugin plants a defect a check must catch. It is not packed and not an example to copy.

| Project | What it holds | Needs |
|---|---|---|
| [Valheim.Testing.NativeAcceptance](.) | The console runner (`validate`, `run`, `campaign check`, `campaign run`, `validate-host`, `host`, `prepare-server`), the plans (`LifecyclePlan` for the two dry-site scenarios, `AcceptancePlan` with the fields below), every scenario by name ([ScenarioTable](ScenarioTable.cs)), the sample plans, session manifest and inventory | .NET 10 SDK; a native run needs the game, Steam and the machines the plan or inventory names |
| [AcceptanceMod](AcceptanceMod/), [AcceptanceMod.Adapter](AcceptanceMod.Adapter/), [Controls](Controls/), [Probes](Probes/) | The mod under test, its test adapter, and the control and probe plugins | A game install (`-p:ValheimPath=`); the adapter and some controls also need the ValheimCLI core (`-p:CliDll=`) |
| [Valheim.Testing.NativeAcceptance.Tests](../Valheim.Testing.NativeAcceptance.Tests/) | Every scenario, control and plan rule against scripted worlds (`TestWorld`, `CampaignWorld`), and the native session tests (`Category=Native`) | .NET 10 SDK; the native tests need a private `session.json` |

The tests compile the example's copyable [GameSessionFixture](../../examples/FullLifecycle/ExampleMod.Tests/GameSessionFixture.cs) from its one source. Native runs prepare their runtimes, world fixtures and clients as the example's [native session](../../examples/FullLifecycle/README.md#prepare-the-native-session) does, with AcceptanceMod and its adapter in place of the example's, and follow its [client modes](../../examples/FullLifecycle/README.md#owned-or-attached-client) and [cleanup limits](../../examples/FullLifecycle/README.md#cleanup-and-its-limits).

```sh
# Every scenario, control and plan rule against scripted replies: no game.
dotnet test tests/Valheim.Testing.NativeAcceptance.Tests -c Release --filter "Category!=Native"

# One plan whose actors are on this machine: check it and copy the fixtures without launching anything, then run.
dotnet run --project tests/Valheim.Testing.NativeAcceptance -c Release -- validate plan.json <new-output-directory>
dotnet run --project tests/Valheim.Testing.NativeAcceptance -c Release -- run plan.json <new-output-directory>
```

## The dry-site scenarios

`dry-site-lifecycle` ([DrySiteScenario](DrySiteScenario.cs), [sample plan](sample-plan-dry-site-lifecycle.json)): AcceptanceMod's `acceptancemod_mark` marks the dry site and refuses the wet one, each asked once; the server's saved objects show it; a client without the mod joins, is protected, arrives beside the marker and sees it; confirmed save, the client leaves, only the owned server restarts; the server still has the marker, the client rejoins and sees it again. `crossplay` runs the same steps with the client joining each boot's crossplay lobby.

### The server half alone

With `"scenario": "dry-site-server"` ([DrySiteServerScenario](DrySiteServerScenario.cs)) the runner runs only the server steps: no marker before, the mod marks the dry site and refuses the wet one (each asked once), the saved objects show it, confirmed save, restart of only the owned server, the marker is still there and the wet site still empty. The plan has no `client`, `review` or `arrival` section; a client section is refused, because nothing here looks from a client. The result says only that the mod and its adapter load on that server and the marker persists; it says nothing about what a client sees.

Nobody needs to prepare a fixture for it by hand:

```sh
dotnet run --project tests/Valheim.Testing.NativeAcceptance -c Release -- prepare-server <server-runtime> <new-output-directory>
dotnet run --project tests/Valheim.Testing.NativeAcceptance -c Release -- run <that-directory>/plan.json <another-new-output-directory>
```

`prepare-server` ([ServerFixture](ServerFixture.cs)) takes a server runtime with BepInEx, ValheimCLI (core, Standard, WorldTools; `[Server] Port = 5577`), AcceptanceMod and its adapter and nothing else in `BepInEx/plugins`. It pins the runtime by hash and those five plugins by MD5, starts one owned server on a copy (the game creates a new world; this boot accepts any world, the plugins stay strictly pinned), reads the world uid and picks the sites from the world generator's heights through ValheimCLI's `valheim.world/terrain-grid` (the toolkit's `SiteSearch`, which reads each grid as a validated `TerrainCapture`), never from the mod: the dry site at least 3 m above the rule's 31.5 m, the wet one at least 3 m below, the closest such samples to the world's centre, 50 m apart. It confirms a save, stops its server and writes `plan.json`. It prints `PREPARED`, never `PASS`: preparing is not the test. The plan holds a generated throwaway server password; share the reports, not the plan.

The repository's [scheduled server checks](../../docker/linux-server/README.md#scheduled-checks-in-ci) run exactly these two commands in the Linux server image every night.

### Human review

With `"review": { "enabled": true, "seconds": 600 }` in a `dry-site-lifecycle` plan, after the automated checks of the second round the runner leaves the player beside the marker, prints `REVIEW:` with what to look at, and waits for you to write `review.json` in the output directory: `{"verdict":"pass","notes":"..."}`. The verdict is recorded in `result.json` as `humanReview`; it never adds a step, so the automated result cannot change because of it, and a missing verdict is recorded as such.

## Review evidence

### Repeatable review stills

[`sample-plan-review-capture.json`](sample-plan-review-capture.json) shows a separate `review-capture` run. It starts one pinned dedicated server and one owned, joined client, protects the disposable character, arrives at the declared dry point, and captures two stills with the same declared weather, time, camera distance, height, azimuth, mist and clutter settings. Change `cameraAzimuthDegrees` if a tree blocks the subject; the two images in a single run always use the same value. It writes `review-first/first.png` and `review-second/second.png` with JSON sidecars recording those conditions, the world UID, game build, plugin pins and each image's SHA-256. Both stills are linked from `result.json`'s `Evidence` by their sidecars (kind `review-still`, see [evidence](../../docs/evidence.md)). `ReviewCapture.Capture` restores the client's prior safety, cheats, weather, time, mist, clutter and camera state after each image, including when capture or transfer fails. The adapter also attempts restoration on unload.

Run it with the normal pinned runner after replacing the sample's paths and hashes:

```sh
dotnet run --project tests/Valheim.Testing.NativeAcceptance -- validate review-plan.json new-review-output
dotnet run --project tests/Valheim.Testing.NativeAcceptance -- run review-plan.json new-review-output
```

The client must run on the runner's machine for this scenario. For a remote client, use its `IGameHost` with `ReviewCapture.Capture` in your own scenario. The capture is **evidence for human review**, never an assertion that the scene looks correct. Review the PNGs yourself and record a verdict separately. Use a fresh output directory and an empty no-space host capture directory for each run. The run keeps the original host PNGs under `capture-host-*` beside the fetched copies for audit; remove the run directory when you have archived the evidence. A failed or interrupted run can leave a host image, but cannot turn it into a passing review result.

### Bounded motion evidence

`ReviewClip.Capture` records a short, opt-in scene-only sequence from a ready, strictly pinned client. The adapter renders a second world camera into a private off-screen texture; it does not record the desktop, account screens, chat or screen-space UI. UI layers and layers used by world-space canvases are excluded, so a mod that places ordinary scenery on a UI layer may leave it out of the clip. Check an actual frame before using the result as evidence. The native 1.0.16 check captured 15 frames at 320×180 and 5 fps in a disposable local world; the inspected frames showed the world without the screen UI. It does not establish that every mod's custom UI is excluded.

The game-side frame source compiles in every adapter that compiles the source package, which therefore references the game's `UnityEngine.UIModule` and `UnityEngine.ImageConversionModule`, as the suite's adapter does (#273 moves it into ValheimCLI's `Observe` pack, which ends that requirement). Registering it is still opt-in. After the client is in the intended world and its player is ready, with ValheimCLI devcommands enabled, register `ReviewClipFrames.Command()` beside `ReviewState.BeginCommand()` and `ReviewState.RestoreCommand()`. Pass its owned client host and the same exact world and plugin pins used for the run:

```csharp
var plan = new ReviewClipPlan(
    Id: "approach-01", ExtensionId: "acceptancemod.testing",
    HostDirectory: @"C:\test-runs\my-run\capture", EvidenceDirectory: evidencePath,
    WorldUid: expectedWorldUid, GameBuild: expectedGameBuild,
    PluginPins: expectedPluginPins, Width: 320, Height: 180,
    FramesPerSecond: 5, Frames: 15);
ReviewClipReceipt clip = ReviewClip.Capture(client, clientHost, plan,
    fetchTimeout: TimeSpan.FromSeconds(30), cancellation);
report.Attach(clip.Evidence); // result.json links the sidecar, kind review-clip
```

The result is a local directory of hashed evidence: the source frames `frame-000.png`, `frame-001.png`, …, the game's `frames.csv` (each frame's elapsed milliseconds, bytes and SHA-256) and a JSON sidecar with actual duration and frame rate and world/build/plugin pins. The host checks every frame against the game's digest before publishing; the receipt's `ManifestSha256` is the SHA-256 of `frames.csv`. Each call needs a fresh host directory and local evidence directory; the 10-second, 60-frame, 640×360 and 24 MiB ceilings (the plan's and the adapter's rules) reject unbounded requests. A failed capture or transfer leaves no passing local evidence. The frame command is an ordinary bounded command (at most the clip's length plus 20 seconds, never under 30); cancellation is checked after it replies and stops the transfer, and a canceled clip is restored and never published. The adapter restores its review state on unload too. The host's owned run directory may retain complete source frames for audit; remove it with the rest of that disposable run. The sidecar starts with `visualVerdict: "not asserted"`; watch the frames and record the human verdict separately. To view them as one animation, use your own tool, for example `ffmpeg -framerate 5 -i frame-%03d.png clip.apng` with the plan's frame rate.

## Native campaign

### Read-only objects at a joined site

[`sample-plan-area-objects.json`](sample-plan-area-objects.json) shows the separate `area-objects` run for issue #201. It joins an owned, pinned client to a disposable dedicated server, arrives at dry ground, then calls `AreaObjectSnapshot.Capture` at a 16 m radius. The server supplies saved ZDO and container rows; the client supplies loaded prefabs and current piece-support rows. The runner writes the complete replies to `evidence/area-objects-001.json` and links it, with its SHA-256, from `result.json`'s `Evidence`. This is optional diagnostic evidence: it checks complete replies and world identity, not whether AcceptanceMod should have placed a particular object. Use the normal `validate` and `run` commands with the sample's paths and hashes replaced. See [object snapshots](../../docs/area-object-snapshots.md) for the contract and privacy limits.

Six more scenarios, and a hosted run, take the toolkit's lifecycle steps and world observations through one native campaign: a real dedicated server owned by the runner (as above, or on another host with `--inventory`, or as a campaign) and a real Windows client. Each plan runs one scenario; together they prove the native acceptance items of issues #20, #23, #24, #26, #30, #31, #32, #33, #34 and #35 that a game can prove, and #91's content census once its native run is done (below). The dry-site feature is unchanged. What the campaign adds is small and kept apart:

- **AcceptanceMod** gains a version handshake adapted from the wiki's [RPC version-handshaking concept](https://github.com/Valheim-Modding/Wiki/wiki/RPC-Version-Handshaking) ([VersionHandshake.cs](AcceptanceMod/VersionHandshake.cs); its net version is a build property, so `-p:AcceptanceModNetVersion=2` builds a mismatched AcceptanceMod), one server-synced config entry with its own routed-RPC sync ([SyncedGreeting.cs](AcceptanceMod/SyncedGreeting.cs), `[Server] Greeting`, changed on the server with `acceptancemod_greeting <word>`), a custom-data key on the player (`acceptancemod_note <word>` writes `acceptancemod.note`) and a saved label on its marker (`acceptancemod_label`), and registered content for the content census ([Content.cs](AcceptanceMod/Content.cs): one item, `AcceptanceMod_SurveyStake`, registered in ObjectDB and as a ZNetScene prefab, one recipe, `Recipe_AcceptanceMod_SurveyStake`, a Hammer-table piece `AcceptanceMod_SurveyPost`, and a status effect `AcceptanceMod_SurveyBlessing`; `-p:AcceptanceModOmit=recipe` and `-p:AcceptanceModOmit=status-effect` build two separate census controls). Unlike the wiki example, a client without AcceptanceMod still joins; only an installed, mismatched copy is refused. Nothing spawns the item. The wiki pattern predates 1.0; the native checks below are the evidence for this mod's behavior on 1.0.16.
- **AcceptanceMod.Adapter** registers the toolkit's adapter commands (`zones`, `custom-data`, `globalkeys`, `globalkey` behind the `ACCEPTANCEMOD_TEST_FIXTURES` gate, `config`, `unresolved-prefabs`, `dungeon-rooms`, `harmony`, `content-census`) and the mod's own marker and ownership commands. It depends on AcceptanceMod only softly and never on its types, so it also loads on a client without AcceptanceMod.
- **[Controls](Controls/)**: four tiny plugins, each a deliberate defect for one check, and two defective AcceptanceMod builds (`omitted-recipe`, `omitted-status-effect`). They are never in a normal runtime: a plan that pins a control plugin is refused unless it names it in `expectFailure`, and then the run passes only if that check fails for the control's named reason (a control whose defect the check cannot see fails the run). The run ends after the expected failure. The plan cannot tell the omitted-recipe build from the normal one by its pins; only the census's failure on that recipe alone shows which one ran.

| Sample plan | Scenario | Server runs | Client runs | Proves |
|---|---|---|---|---|
| [lifecycle-world](sample-plan-lifecycle-world.json) | `lifecycle-world` | AcceptanceMod, adapter; `environment.ACCEPTANCEMOD_TEST_FIXTURES=1` | AcceptanceMod, adapter | #30 census of AcceptanceMod's patches; #23 a boss key set once on the server reaches the client and survives the save and restart; #24 a vanilla dungeon's saved rooms lie in its location's zone, and its interior offset; #35 the zone cycle (the client unloads the marker's zone and loads it again, the marker's saved label is back) and the logout cycle (the character file is rewritten, this run's `acceptancemod.note` comes back) |
| [vanilla-client](sample-plan-vanilla-client.json) | `vanilla-client` | AcceptanceMod, adapter | adapter only, AcceptanceMod pinned `absent` | #33 a client without AcceptanceMod beside its objects resolves every prefab hash and logs no missing prefab, missing RPC handler or unload error, in both rounds |
| [synced-config](sample-plan-synced-config.json) | `synced-config` | AcceptanceMod, adapter | AcceptanceMod, adapter | #20 both sides hold the same greeting after the join; an admin change reaches the client within the wait (woken by AcceptanceMod's "received" line in the owned client's live log); after a restart the client gets the server's saved value again |
| [refused-join](sample-plan-refused-join.json) | `refused-join` | AcceptanceMod (net version 1), adapter | first `refusedClient`: AcceptanceMod built with `-p:AcceptanceModNetVersion=2`; then `client`: the server's AcceptanceMod and adapter | #34 the mismatched client is refused with `expectedRefusal` (default `ErrorVersion`, 3) read back at its menu, and the server then keeps a matching client |
| [crossplay](sample-plan-crossplay.json) | `crossplay` | AcceptanceMod, adapter; `"crossplay": true`, an explicit `-port`, no `-password` | AcceptanceMod `absent`, `"crossplay": true` | #32 the dry-site lifecycle joined through each boot's PlayFab lobby; the report records `crossplay` and `clientJoin` |
| [hosted](sample-plan-hosted.json) | `hosted` (the runner's `host` mode) | none | AcceptanceMod, adapter, `hostWorld` | #31 AcceptanceMod's feature on a host (mark, refuse, saved objects, persistence across the host's restart) and AcceptanceMod's broadcast handler running in the host's own process |
| [content-census](sample-plan-content-census.json) | `content-census` | AcceptanceMod, adapter | AcceptanceMod, adapter (the server's builds) | #91/#114 in each round (first, after restart and rejoin) each side observes AcceptanceMod's declared item, prefabs, recipe, Hammer-table piece and status effect ([content-expectations.json](content-expectations.json)); dependencies resolve, no undeclared content appears in scope, and the observation comes from that side's pinned AcceptanceMod build. The #114 additions passed a native run. |
| [ownership-handoff](sample-plan-ownership-handoff.json) | `ownership-handoff` | AcceptanceMod, adapter | Two simultaneous owned clients, each with AcceptanceMod and adapter on a different host and Steam account | #211 A explicitly claims the marker; B sees A's owner; A leaves; B explicitly claims it; server and B agree on one owner. This tests orchestration, not automatic ownership of every Valheim object. Native Windows dedicated server + macOS/Windows clients passed 47/47 steps (Valheim 1.0.16) |
| [ghost-protection](sample-plan-ghost-protection.json) | `ghost-protection` | AcceptanceMod, adapter | Two simultaneous owned clients as for ownership-handoff | #261 a creature another client owns and simulates never targets a protected player, and that client reads the player as a ghost; with ghost mode off the same creature targets it (the control); after a rejoin it is protected again. Needs ValheimCLI's Standard pack on every process |

Run `ownership-handoff` as a campaign ([Prepare the campaign](#prepare-the-campaign)) with the [environment inventory](sample-environment-inventory.json). It lists client hosts in preference order; binding the prepared actors sets each client's install and `port` from its assigned environment. Each host has its own game install and disposable local character. Sign into Steam on each client host first; preflight reads the account in use and rejects a conflict. No Steam ID or account-pool file is needed in the inventory:

```json
{
  "hosts": { "gaming-pc": { "...": "host settings" }, "second-host": { "...": "host settings" } },
  "environments": [
    { "name": "client-a", "host": "gaming-pc", "roles": ["client"], "install": "<client A install>", "runtime": "<client A run directory>", "cliPort": 5556 },
    { "name": "client-b", "host": "second-host", "roles": ["client"], "install": "<client B install>", "runtime": "<client B run directory>", "cliPort": 5557 }
  ],
  "leaseHost": "gaming-pc",
  "leaseDirectory": "<shared lease directory on gaming-pc>"
}
```

Run `campaign check` first: two clients on one host, a CLI port conflict, two clients with one registered character, or a plugin the plan pins but a role's lock does not select is refused before any host is contacted; a shared signed-in Steam account is refused by `session check --hosts` and again before the first copy. The suite's own plan rules (a missing capability, one character name twice) are checked on the bound plan before any game starts. Then `campaign run` with the same manifest and plan; the ordinary `run` refuses these two scenarios. The inventory stays private; the [sample plan](sample-plan-ownership-handoff.json) uses placeholder file hashes and a documentation-only server address. Each client joins with the toolkit's one join (`SessionControl.JoinWorld`: world pins, the world awaited, the player protected, test access on its disposable character), and arrives with the toolkit's one arrival, `PlayerPlacement.Arrive` without a server, so the client teleports itself: with two players the server cannot name one.

Give the clients separate landing points at least 3 m apart, each beside the marker rather than on it. Before either client launches, the server checks the declared heights against the fixture's generator and refuses wet targets. After each teleport's floor-ready signal, the client measures loaded terrain at its target (`Arrive`'s `loadedGround`), which may include location levelling absent from the raw generator, and the scenario refuses it when it is not dry. That measured height is a placement input, not a terrain-correctness assertion; a separate player-support observation must still confirm proximity, grounding and low speed. Failed arrival keeps a bounded read-only support/ground diagnostic before teardown.

The runner issues each join, local teleport and ownership claim once. Both clients must list `PlayerPlacement.ArrivalCapabilities` (`valheim.session/teleport-signals`, `valheim.world/player-support-wait`, `valheim.world/player-support`) and `valheim.world/terrain` in `capabilities`. ValheimCLI's game-side teleport waits and the adapter's marker-owner notification wait complete those transitions; a lost action reply is not retried. The adapter records the marker's ZDO owner and the observing process's ZDO session ID, so the server and B can agree on the same owner rather than infer it from peer order. The ownership hook is test-only and belongs in the example's adapter; shared doubles do not model Valheim's ownership rules. A listen-server version needs a separate host plan because one host is also a player; this example covers a dedicated server. Before a native run, verify the required Standard CLI commands in the pinned build too; [#217](https://github.com/tvongaza/ValheimTesting/issues/217) tracks making that preflight automatic.

**Ghost protection (#261).** A creature's AI runs only in the process that owns its ZDO, and the game kept ghost mode in a field of the player's own process, so a creature another client simulated could see and attack a player protected only in its own game. ValheimCLI's Standard pack now writes ghost mode to the player's ZDO and reads it there for every player the process does not own, and `PlayerPlacement.Protect` refuses a reply without `ghostReplicated=True`. `ghost-protection` runs as a campaign like `ownership-handoff`: B joins, arrives and spawns a Skeleton beside itself (B owns it); A joins protected and arrives within its view, 3 to 15 m from B; for 20 s B's process reads the Skeleton, still owned by B, and A. The run passes only when B reads A as a ghost and the Skeleton never targets A. Then A's ghost mode goes off and the same Skeleton must target A within the window, which shows the watch could see an attack. Last, A leaves (its menu pins checked) and joins again, protected by the join, and the Skeleton again never targets A. B removes the Skeleton and both clients stop in every outcome. A client plan's `"targetable": true` opts a player out of ghost mode for AI and aggro checks (`cli_set_player_safety true targetable`).

| Control ([Controls](Controls/)) | Install on | `expectFailure` in | Check that must fail, and the reason it must name |
|---|---|---|---|
| [MissingHarmonyTarget](Controls/MissingHarmonyTarget/Plugin.cs) (`valheimtesting.acceptancemod.control.missingtarget`) | server | lifecycle-world | #30 the census names its missing patch (`not applied: postfix (any method) on Player::AcceptanceModControlMethodThatDoesNotExist`), and #26 the log scan's defaults fail on the server's live BepInEx log with `accesstools-not-found` naming the method. On the Valheim 1.0.16 Windows dedicated server (BepInEx 5.4.23.5, HarmonyX 2.9.0) that warning is all BepInEx's log has; `PatchAll` then throws `Undefined target method`, which only Unity's log (`-logFile`) records and the teardown scan counts as `harmony-undefined-target`. The control logs `MissingHarmonyTarget: PatchAll returned` right after `PatchAll`, and the run records whether that line is there (`controlPatchAllReturned`; natively `false`) without failing on it. Its plan names both lines as expected (below) |
| [ServerOnlyPrefab](Controls/ServerOnlyPrefab/Plugin.cs) (`valheimtesting.acceptancemod.control.serveronlyprefab`) | server | vanilla-client | #33 the runner spawns its object beside the dry site (`acceptancemodcontrol_spawn`), and the vanilla client's census names its hash and `AcceptanceModControl_ServerOnly` |
| [FieldOnlyState](Controls/FieldOnlyState/Plugin.cs) (`valheimtesting.acceptancemod.control.fieldonlystate`) | client | lifecycle-world | #35 a value kept only in a component field on the marker `did not survive the zone reload`, while AcceptanceMod's saved label did |
| [SuppressedProfileSave](Controls/SuppressedProfileSave/Plugin.cs) (`valheimtesting.acceptancemod.control.suppressedsave`) | client | lifecycle-world | #35 the logout check times out waiting for the character file `to be rewritten by the logout` |
| `omitted-recipe`: AcceptanceMod built with `-p:AcceptanceModOmit=recipe` (a build, not a plugin) | server and client, pinned as `valheimtesting.acceptancemod` | content-census | #91 `the census fails only on the omitted recipe`: `Recipe_AcceptanceMod_SurveyStake` missing on the server and on the client, and nothing else wrong; any other failure, or a census that passes, fails the run |
| `omitted-status-effect`: AcceptanceMod built with `-p:AcceptanceModOmit=status-effect` | server and client, pinned as `valheimtesting.acceptancemod` | content-census | #114 `the census fails only on the omitted status effect`: `AcceptanceMod_SurveyBlessing` missing on both sides, with no unrelated failure |

A control run is the scenario's own plan plus the control's pin (by MD5, on the side the table says) and `"expectFailure": "<name>"`. Both of MissingHarmonyTarget's lines fail the teardown log scan by default, so its plan also names them as expected, and the scan counts them apart instead of failing the run a second time on the defect the control's own check already required: `"logScan": { "accesstools-not-found": { "expected": ["AcceptanceModControlMethodThatDoesNotExist"], "reason": "..." }, "harmony-undefined-target": { "expected": ["MissingHarmonyTarget.Plugin+PatchMissingMethod"], "reason": "..." } }`. The plan is refused without them.

**Probes** ([Probes](Probes/)) plant no defect and need no `expectFailure`; pin them like any plugin. [QuitLog](Probes/QuitLog/Plugin.cs) (`valheimtesting.acceptancemod.probe.quitlog`) logs a warning in `OnApplicationQuit` and one in `OnDestroy`, so a run shows its teardown log scan reading what a mod logs while the game quits (#85): the scan counts them as `unknown-warning`, the first as its first line. BepInEx 5.4 flushes its disk log every 2 seconds and not again at quit, so a boot's last lines can be missing from the kept log even after a clean stop ([log scan](../../docs/packages/Valheim.Testing.Game.md#log-scan-at-teardown)); the adapter therefore enables the toolkit's `QuitLogFlush`, and a plan's `environment.ACCEPTANCEMOD_TEST_NO_QUIT_FLUSH=1` turns it off as the negative control.

**Adapter unload (#30).** A dry-site-server plan may add a `patchReload` section: after the restart, the runner copies the [PatchReload](Probes/PatchReload/Plugin.cs) probe (`valheimtesting.acceptancemod.probe.patchreload`) into ScriptEngine's `BepInEx/scripts`, replaces it with a second build, then deletes it, and after each change reads the whole Harmony census. The probe patches `Terminal::InitTerminal`, the method AcceptanceMod patches, under its own ID and unpatches only that ID when unloaded (`UnpatchSelf`), as an adapter should; every other owner's patches must be exactly as before, and AcceptanceMod's declared patch applied. Each change waits for ScriptEngine's own "reloaded" line before it reads the pins: the pins hash the file, which changes before the reload, and while the reload runs the probe is briefly not loaded. The runtime needs ScriptEngine in its plugins, pinned, with `LoadOnStart` and `EnableFileSystemWatcher` on and an empty `BepInEx/scripts`; the probe is not pinned (the scenario pins each build as it loads). Build the probe twice (`-p:ProbeRevision=B` for the second) and name both:

```json
"patchReload": { "revisionA": "<full path>/A/AcceptanceMod.Probe.PatchReload.dll", "revisionB": "<full path>/B/AcceptanceMod.Probe.PatchReload.dll", "reloadSeconds": 60 }
```

The control is the `-p:ProbeUnpatch=Other` build as `revisionA` with `"expectOthersRemoved": true`: its unload also removes AcceptanceMod's patches (`Harmony.UnpatchID("valheimtesting.acceptancemod")`), and the check after the reload must fail naming them. Not `Harmony.UnpatchAll()`: that removes ValheimCLI's patches too, and no CLI command ran after it, so the run lost the census it reads; the teardown scan's `harmony-unpatch-all` failure is what names that case. Local runs only (no `--inventory`): the scenario writes into the runtime copy. Each census is kept as `patch-reload-{stage}.txt`.

Every scenario is a function of the runner's `GameSession` and the plan, `(session, plan)`, listed once by name in [ScenarioTable](ScenarioTable.cs) (how it runs, whether it marks the sites, and a campaign scenario's named clients); `Program.cs` hands `ScenarioTable.Run` to the runner, and `campaign run` is `PinnedServerRun.RunCampaignAsync` with the entry's clients. The integration tests ([CampaignScenarioTests](../Valheim.Testing.NativeAcceptance.Tests/CampaignScenarioTests.cs), [HostedScenarioTests](../Valheim.Testing.NativeAcceptance.Tests/HostedScenarioTests.cs), [CampaignPlanTests](../Valheim.Testing.NativeAcceptance.Tests/CampaignPlanTests.cs)) run every scenario on a `FakeGameSession` over a scripted world, each control's expected failure, each control whose check would pass, and every plan refusal against scripted replies; they read the sample plans too.

### Prepare the campaign

For a short server plus two-client setup smoke, use the runnable
[`ThreeActorSmokeScenario.cs`](ThreeActorSmokeScenario.cs),
[`sample-plan-three-actor-smoke.json`](sample-plan-three-actor-smoke.json) and
[`sample-three-actor-campaign.json`](sample-three-actor-campaign.json). The scenario owns one
dedicated server and two clients, joins both clients to the same pinned world, verifies their plugins and world identity,
then checks that the server sees two peers. Its named checkpoints require both clients at their menus, both joined,
client B back at its menu while A stays in the world, and both joined again. Each transition is requested once and
the next checkpoint reads the resulting state; a failed or uncertain transition is not retried. This tests setup and
rejoin, not marker ownership or gameplay. The underlying
`HostedCampaignPreparation` accepts any number of named clients beside one dedicated server: each role is a separate
lock/character object, and `ApplyTo` binds a dictionary of client plans by name. The suite binds only
`client-a` and `client-b` because its assertion expects two peers. A different mod can bind three or more without
changing preparation. Simultaneous clients need different signed-in Steam identities, hosts and registered character player IDs.

The server and every client prepare their runtime in parallel, with one claim per host. After the server's ready
checkpoint, the session's `OpenClientsAsync` launches the named clients concurrently and waits for all of them at the
menu checkpoint. The join and rejoin checkpoints are explicit so a test can pause one actor while the others stay in
the world. A failed client start closes the other successful starts before teardown; it does not advance the test. Each client's evidence (its process and command records and kept logs) is in its own folder, `client-a/` and `client-b/` in the run's output.

The same two scenarios also run as xUnit tests on a native session: [GameSessionFixture](../../examples/FullLifecycle/ExampleMod.Tests/GameSessionFixture.cs) is a copyable `IAsyncLifetime` adapter (one session per test class) that runs the toolkit's runner up to the scenario, hands the started `GameSession` and plan to the tests, and lets the run tear down and report when the class finishes; a run that never started, or ended with a failed step or cleanup, fails the class with its exit code. [AcceptanceSession](../Valheim.Testing.NativeAcceptance.Tests/AcceptanceSession.cs) finds its environment by convention: `session.json` (the campaign manifest below) and `<scenario>.plan.json` beside this suite's test project, read in place and never committed. Without them each native test FAILS with that reason (#258 Q7: a native gate that skipped silently would approve itself), so they carry the trait `Category=Native`: validation runs the suite's other tests with `--filter Category!=Native`, and a native run is `dotnet test tests/Valheim.Testing.NativeAcceptance.Tests -c Release --filter Category=Native`. Each run writes a new evidence directory under `session-runs/` in the test output.

Copy the sample campaign and inventory to a private test directory. In the campaign manifest, name the private environment
inventory, fixture world, direct-join address, and a reviewed dependency lock for each role. Create each lock from an
explicit `NativeDependencyRequest` with `dotnet run scripts/native-dependencies.cs -- resolve request.json lock.json`;
the lock selects the mod, its hard dependencies and one coherent ValheimCLI core and packs. The server lock must include
WorldTools because the two-peer checkpoint calls `cli_peers`: the plan pins `valheimCLI.worldtools`, and `campaign check`
refuses a plugin the plan pins but the role's lock does not select, before the game starts. If the source install's loader is unsuitable, set `loaderPackage` on that campaign role to a reviewed
`BepInExLoaderPackage` manifest. The existing package API captures and validates its files and hashes; campaign preparation
applies them only to the disposable copy. Do not repair the live source install or copy individual Doorstop files by hand.
A nested `BepInEx/core/core` can make the preloader load Harmony twice and abort before writing its main log.

The [environment inventory template](sample-environment-inventory.json) lists the reusable machines
in preference order. Replace its host addresses and paths. Sign into Steam on each client host beforehand;
the runner reads the signed-in identity during host preflight, refuses two clients on the same account, and checks
the running game's own identity after launch. It never changes the login. The prepared environment, kept in memory,
leases one entry per observed identity. Inventories that may use the same accounts must use the same `leaseHost` and
`leaseDirectory` to coordinate their leases. Set
`"inventory": "environment-inventory.json"` in the campaign manifest. Each campaign role
may list `environmentCandidates` in preference order, or omit it to consider all compatible recipes in inventory
order. `differentHostFrom` names actors that must run on another host; for example, client B may require a host
different from client A while the dedicated server shares client A's machine. Resolution backs up to another recipe
if an earlier choice leaves a later actor without a compatible host. A recipe's `roles` is `["server"]` or `["client"]`.
Run `valheim-test session check` first to see each chosen recipe and its reason. `--hosts` adds read-only checks of
the selected installs, ValheimCLI ports and signed-in Steam accounts. Preparation writes
`environment-assignments.json` to the output's `prepared/` so the choice is reviewable afterwards; the observed Steam IDs
are never written. The source installs remain untouched and all actor runtime copies are made after the full preflight passes.
Rented GPU VM client environments are experimental in this campaign flow; start with local or known SSH desktops.
They still require an interactive desktop, a signed-in Steam client and the same preflight and lease checks.

Campaigns discover the Steam IDs from the selected hosts; no account file or pinned ID exists. Preparation checks identity before copying;
launch repeats that check under the account lease, then verifies the game's
own identity. Unix's remembered Steam login alone is not proof of the running game's account. Identity values are redacted
from the recorded identity reply. The suite's runner opts into `TestAccess.Ensure`: dedicated servers acknowledge
cheats locally, and clients acknowledge their disposable character after joining. `AllowOnServerClients` must already be
set in the staged configuration for client mutations; the helper never grants it at runtime. Register two clean, distinct
test characters with `DisposableCharacterStore`; one seed copied twice is still one player. Each is staged into its client host's own `characters_local`, resolved on that host with its Steam `userdata` (see [the campaign docs](../../docs/packages/Valheim.Testing.GameSessions.md#a-campaign-remote-clients-and-steam-identities)); `session check --hosts` prints both folders. Set the server password and
client password variable in the private plan/environment as for any owned-server run. The sample paths and password are
placeholders, never defaults that the runner guesses.

From the repository root, these are the preparation check and the complete run:

```sh
dotnet run --project src/Valheim.Testing.NativeSmoke -c Release -- session check /private/test/campaign.json --hosts
dotnet run --project tests/Valheim.Testing.NativeAcceptance -c Release -- campaign check /private/test/campaign.json /private/test/three-actor-plan.json
dotnet run --project tests/Valheim.Testing.NativeAcceptance -c Release -- campaign run /private/test/campaign.json /private/test/three-actor-plan.json /private/test/runs/first
```

The first line is `valheim-test session check SESSION --hosts`, run from the NativeSmoke project or the
installed `valheim-test` tool; the same lines work in any shell (a sandbox with a blocked NuGet cache: see the recipe in [AGENTS.md](../../AGENTS.md)). Use `--json` for a machine-readable report. It
reports all independent local lock, loader, character, fixture and inventory problems it can find in one pass, plus the
selected actors and hosts. `--hosts` reads the selected source installs and Steam sessions on their hosts,
and checks the combined copy size of actors sharing a target volume against its free space;
it does not copy or launch anything. Without that flag it does not contact hosts or assert their current readiness.
`campaign check` adds the plan's agreement with the campaign (world and join set, pinned plugins selected, no placeholder argument) without touching a host; the suite's own plan rules run once the plan is bound to its prepared actors; `campaign run` starts with the same two checks as its Preflight steps. A ready host result is still checked
again under leases before launch. The run
takes one lock per host, checks for conflicting client or owned-runtime processes, and prepares all named actors concurrently, even when a server and
client share a host. Each actor gets its own clean install copy, selected mod and ValheimCLI files, and the clients get
separate registered character names. The runner waits for every preparation to settle before it starts the server;
on failure it retires every copy whose ownership was established. It then derives strict pins and runs the plan through
`PinnedServerRun.RunCampaignAsync`, with the prepared environment in memory. The campaign also copies the manifest's one-world fixture into the dedicated server's
`worlds_local` layout. The original fixture stays read-only; this layout matters because Valheim silently creates a
new world when a world of the requested name is absent from `-savedir/worlds_local`.
Once every process the run started has stopped, one owner retires what it left: the disposable characters, then each
prepared install, each its own Cleanup step, under the host locks the run already holds and with one game-process check
per host. If a process is still active or a stop cannot be established, the guarded cleanup refuses to remove its
character or install and names what remains for inspection.
Ctrl+C or SIGTERM cancels preparation as well as the game phase. Preparation waits for sibling actors to settle,
then attempts cleanup without the cancelled token; it reports any unproven cleanup rather than claiming the host is free.
An abrupt runner crash still needs the durable recovery workflow tracked in #257.
The output holds the normal `result.json` and JUnit evidence, whose Preflight, Setup and Cleanup steps carry the
preparation and retirement seconds, and `prepared/` with the assignment report and role-specific CLI manifests. No game
startup is part of the `check` result.

1. **Builds.** Build AcceptanceMod twice: normally, and with `-p:AcceptanceModNetVersion=2` into another folder (`-o`), for the refused client; for the two content-census controls, build separate copies with `-p:AcceptanceModOmit=recipe` and `-p:AcceptanceModOmit=status-effect`. Build the adapter and the controls you run (FieldOnlyState needs `-p:CliDll=`, as the adapter does). Pin every DLL by MD5 in the plans.
2. **The server runtime** as in the example's [native session](../../examples/FullLifecycle/README.md#prepare-the-native-session), with ValheimCLI's core, Standard and WorldTools packs, AcceptanceMod and the adapter; pin all five in `pins`. A control run adds the control plugin to `BepInEx/plugins` and to `pins`.
3. **A fixture world** that has never been marked, with a vanilla dungeon near the sites: `dungeon` is a dungeon location's ground position within two zones (128 m in x or z) of the arrival point, so the server generates its rooms while the player stands there (the step waits up to the client's `arrivalSeconds`). To find one, turn devcommands on on a server with the fixture world and run ValheimCLI's `cli_world_dump`; its locations list names each location with its position. Pick a dungeon location, such as `Crypt2` to `Crypt4` or `TrollCave02` (Black Forest) or `SunkenCrypt4` (Swamp), with dry ground (at least 31.5 m) for the dry site and arrival point beside it, and wet ground elsewhere for the wet site. `away` is dry ground at least 5 zones (320 m in x or z) from the dry site, where the player goes so the client unloads the site (more if the client's simulation distance is larger than the default; the run reads it and refuses before moving). `globalKey` is a key the world does not have yet (`defeated_eikthyr` for a fresh world).
4. **Client installs**, each with BepInEx and ValheimCLI's core, Standard and WorldTools packs (`AllowOnServerClients = true`), plus what the table says. One install can serve several plans if you change its plugins between runs; `refused-join` needs two installs, one per AcceptanceMod build, run one after the other. Pin each install's game build, loader and patchers in `installPins`. Each owned client runs from a disposable copy of its install with the toolkit's pinned ValheimCLI set staged in it (`--in-place` before the mode runs the installs as they are; [GameSessions](../../docs/packages/Valheim.Testing.GameSessions.md#an-owned-client-on-this-machine-runs-from-a-disposable-copy)).
5. **A disposable local character** (`characters_local`, never Steam Cloud), existing and used only for tests; `logout.charactersDirectory` is that folder's full path. The logout scenario rewrites it, and the SuppressedProfileSave control keeps it from being saved at all.
6. **Crossplay.** A separate fixture server, private (`-public 0`) and without `-password` (the crossplay join cannot send one), on a game port (`-port`) that no other server behind the same public IP uses: a crossplay lobby is keyed by public IP and port, and a second server on that port takes the first one's joins. Do not switch one fixture between Steam and crossplay.
7. **Hosted.** A small fixture world for the host, pinned in `hostWorld`: Valheim 1.0's chunked save (one `<name>/` directory) or the older `<name>.fwl` and `<name>.db`; the runner places a copy in the client's `worlds_local` (refusing a name already there) and moves it into the evidence afterwards.

Start each plan from its sample and replace every `<...>`: paths, SHA256 manifests (`WorldFixture.Manifest`), `InstallPins.Of`, MD5s and the world UID. Validate first; nothing launches:

```sh
dotnet run --project tests/Valheim.Testing.NativeAcceptance -c Release -- validate lifecycle-world.json <new-output-directory>
dotnet run --project tests/Valheim.Testing.NativeAcceptance -c Release -- run lifecycle-world.json <new-output-directory>
# The hosted scenario has no dedicated server: the runner's hosted modes, where the session's host is the client.
dotnet run --project tests/Valheim.Testing.NativeAcceptance -c Release -- validate-host hosted.json <new-output-directory>
dotnet run --project tests/Valheim.Testing.NativeAcceptance -c Release -- host hosted.json <new-output-directory>
```

### Evidence

Every run writes `result.json` and `junit.xml` with each step, as above. The scenarios add, per round: `{round}-arrival.json`; `first-global-keys.json`, `first-dungeon-rooms.json` (rooms, location, interior offset; also `dungeonInteriorOffset` in the provenance), `first-zone-cycle.json`, `after-restart-logout.json`; `{round}-vanilla-client-1.json`, `{round}-vanilla-client-logs.json`; `first-greeting-joined.json`, `first-greeting-changed.json`, `after-restart-greeting-after-restart.json`. `refused-join` keeps each client's command record and logs in `refused-client/` and `matching-client/`, and its provenance has `refusal`. A control run records `expectFailure`, `controlFailure` (the expected failure's message) and `control`; the MissingHarmonyTarget run writes `control-log-scan.json` (the scan of the server's live log with the default classifications), `controlScanFailure` (that scan's failure), `controlLogLine` (the `accesstools-not-found` line naming the control's method, found among every such line, since other plugins log them too) and `controlPatchAllReturned` (`true` when the control's line after `PatchAll` is in the log, so `PatchAll` did not throw; `false` when it is absent, so it threw), the FieldOnlyState run `first-field-only-state.json`. The content-census scenario writes `{round}-content-census.json` (the expectations, each side's census as it reported itself, and the report) and records `contentExpectations` in the provenance.

### Native results

The campaign ran on 30 September 2026 against Valheim 1.0.16 (BepInEx 5.4.23.5), with this mod under its earlier name, MyMod (plugin `example.mymod`, adapter `example.mymod.testadapter`, commands `mymod_*`, variables `MYMOD_TEST_*`); the results quote the names it had then:

- **lifecycle-world** passed 47 of 47 steps: MyMod's Harmony census; the zone cycle (the client unloaded the marker's zones and loaded them again, and the marker came back with its saved label); the logout cycle (the character file was rewritten and this run's note came back after rejoining); `defeated_eikthyr` set on the server, listed on the client and kept across the save and restart; a `SunkenCrypt4`'s saved rooms (1.0 `roomData`) all in its location's zone, with an interior offset of about (0.63, 5001, 0.26).
- **Each control** failed its check for the named reason: the census named MissingHarmonyTarget's missing patch; FieldOnlyState's field-only value was lost on the zone reload while MyMod's saved label came back; with SuppressedProfileSave the logout check found the character file unchanged; the vanilla client's census named ServerOnlyPrefab's object by its hash, `1334784479` (`MyModControl_ServerOnly`). The `controlPatchAllReturned` record was added after that run.
- **A second pass the same day** (clean stops, the teardown scan's new defaults): lifecycle-world passed 47 of 47 again and gave the clean-run counts in the [log scan](../../docs/packages/Valheim.Testing.Game.md#log-scan-at-teardown) docs. MissingHarmonyTarget passed 13 of 13: the census named the missing patch, the default scan of the server's BepInEx log failed on `accesstools-not-found` naming the method, `controlPatchAllReturned` was `false` (`PatchAll` threw; its `Undefined target method` error was in Unity's log only), and the teardown scan counted both named lines as expected. Without the Unity log's line named, the same run's teardown scan failed on it. The server half with the QuitLog probe passed 17 of 17: one boot's kept BepInEx log had both QuitLog warnings (the scan's first `unknown-warning`); the other boot's had neither, although it quit cleanly, which is BepInEx's disk log flushing every 2 seconds.
- **synced-config** passed (the server's change reached the client, and the client got the saved value again after a restart); **vanilla-client** passed (a client without MyMod resolved every prefab hash near the site); **refused-join** passed (the mismatched client was refused with `ErrorVersion` (3), and a matching client then joined).
- **Adapter unload (#30)**, dry-site-server with `patchReload` (ScriptEngine 11.1 on the 1.0.16 Windows server): 24 of 24 steps. The probe's postfix sat beside MyMod's on `Terminal::InitTerminal`, was replaced by the second build and then removed, and every other owner's patches (MyMod's four and ValheimCLI's) were unchanged after each change. The control (`-p:ProbeUnpatch=Other`) failed as named: `removed: example.mymod postfix MyMod.Plugin+RegisterCommands::Postfix() on Terminal::InitTerminal()` and MyMod's three other patches. An earlier control with `Harmony.UnpatchAll()` took ValheimCLI's patches with it and no CLI command ran afterwards.
- **Quit-time log lines (#99)**, dry-site-server with the QuitLog probe: with the adapter's `QuitLogFlush`, every boot's kept BepInEx log had both QuitLog warnings and ValheimCLI's `Command server stopped` (14 server boots over seven runs, and the client of a dry-site-lifecycle run after its clean stop), each with `Quit log flush armed by MyMod.TestAdapter OnApplicationQuit.`; with `MYMOD_TEST_NO_QUIT_FLUSH=1`, one boot of six lost its last two lines although it quit cleanly.
- **hosted** passed 17 of 17 steps, with the fixture in Valheim 1.0's chunked layout.
- **crossplay** passed 39 of 39 steps with the server on a remote Linux host and the Windows client started in its desktop session over SSH (then through `--profile`, since replaced by `--inventory` and campaigns): the lobby opened and the client joined through PlayFab, before and after a server restart.

- **ghost-protection (#261)**, 6 October 2026, Valheim 1.0.17: a Windows dedicated server, client A on the same Windows PC and client B on a Mac, each with its own Steam account. With the earlier ValheimCLI (80fb6ce), the bug reproduced: B's Skeleton, owned by B, targeted protected A 10.7 s into the window, B read A with `ghost=False`, and A's health fell to 1. With ValheimCLI 8428b69 (the bundle `cli-dependency.json` pins) the run passed 63 of 63 steps: B read A as a ghost, the Skeleton (owned by B throughout) targeted nobody and A kept 25 health; in the control, with A's ghost mode off, the same Skeleton targeted A after 10.6 s; after A's rejoin B read A as a ghost again and the Skeleton targeted nobody.
- **content-census (#91/#114)**: the new piece/status-effect slice and its omitted-effect control pass controlled integration tests but await a joined-client native run. See [Registered content census](../../docs/packages/Valheim.Testing.Game.md#registered-content-census).

### Limits

The in-run log reads (the vanilla client's log scan, the synced-config wake-up, the MissingHarmonyTarget scan, the hosted broadcast line) read live files on the runner's machine: an attached client's are its operator's, so those parts are skipped and the provenance says so, and a run with its server on another host skips the server's. The crossplay lobby is the exception: there it is read in the server host's own log. Not covered natively: a second client joining a host (#31; the session's peer join, `joinsHost`, is tested with scripted replies, and a two-machine run needs a campaign without a dedicated server), an oversized room in a custom test dungeon (#24's negative control; the check is covered with scripted data), overlapping terrain edits across a restart (#24). The game-side projects (AcceptanceMod, the adapter, the controls and the probes) compile only against a game install: of them, only the scheduled server checks build AcceptanceMod and its adapter, in the Linux server image; CI runs the scenarios and plans against scripted replies.
