# Actors and pins

How a test talks to a game process through `GameActor`, what the strict pins are, and how each pin is computed and enforced. Waits and startup are on [Waiting](game-waiting.md), stopping on [Owned server session](game-server-session.md); the install checks before a launch are on [Owned-run preflight](game-preflight.md).

## Actors, fixtures and reports

| Type or member | Does | Fails or refuses when |
|---|---|---|
| `GameActor(name, transport)` | Wraps an existing transport; never owns or stops the process. Verify pins first, issue a mutation once, then poll its read-only observation. | An unknown outcome is a failure, never permission to resend. |
| `GameActor.CommandTimeout` | Bounds one command. Keep it below the scenario allowance: a synchronous read cannot be interrupted by an observation deadline. | |
| `WorldFixture.Copy(source, outputParent, expectedHashes)` | Copies a fixture into a new directory of its own and verifies the copied bytes against the manifest. | A link, an unexpected file or a hash mismatch. |
| `WorldFixture.CopyAsFound(source, outputParent)` | Records and verifies the hashes it finds; a standalone runner binds them as pins. | |
| `WorldFixture.Preserve` | `true` keeps the copy for debugging; otherwise disposing deletes only that copy. | |
| `ScenarioReport` | Records every step, teardown failure and attached evidence in `result.json` and `junit.xml` ([evidence](evidence.md)). Put fixture, build and config hashes in `Provenance`. Use a new output directory for each run; `Write` overwrites what is there. | |

Stop the owned game before disposing its world fixture. The fixture owns no accounts, characters, ports or machine reservations. Output may contain player and world data; inspect it before publishing, and keep credentials out of fixture configuration and logs.

## Strict pins from tests

Every way in reaches the same game command, `cli_expect --strict key=value ...`:

| From | Use |
|---|---|
| The `valheim-cli` executable | `valheim-cli --expect-strict /private/pins.txt ...` (the flag is `--expect-strict`, not a global `--strict`) |
| A YAML plan | `game.expect` with `game.expectStrict: true` |
| A test | `GameActor.VerifyEnvironment(expectationCommand)` |

`VerifyEnvironment` normalizes the expectations to strict mode even if you omit the switch. Every later command rechecks the same pins before dispatch; drift invalidates the actor and blocks the action. Strict mode rejects unlisted loaded plugins and worlds; it does not make `any` an exact build pin, so supply reviewed full hashes and the intended world identity. The preflight is a round trip, not a lock on game state. For dispatch-time enforcement as well, configure ValheimCLI's `[Expectations]` guard; a test never disables it.

`Execute(command)` returns a `GameReply` and requires it accepted by default: the transport completed **and** no line is a known refusal (`That command is a cheat`, `Error executing command`, `Unknown command`, or a ValheimCLI `ERROR:` line), because a refused console command still completes on the transport.

| `GameReply` member | Does |
|---|---|
| `Line(prefix)`, `Lines(prefix)` | Read the reply by prefix |
| `RequireLine(prefix, failure)` | Fails with `failure` when no line has the prefix |
| `Execute(..., requireAccepted: false)` | Lets a test inspect an expected refusal; it cannot bypass a pin failure |

Acceptance supplements a step's semantic check; it never replaces one.

A reload test supplies a pins file, advances the changed plugin's expected hash from the artifact it installs, and requires `absent` after removal. While the transition settles only `cli_expect --strict` is rechecked. Pass `WaitForEnvironment` the reload's own log line through a `LogWait` ([waiting](game-waiting.md#waiting)), so it rechecks when the plugin has loaded rather than on a timer:

```csharp
await actor.WaitForEnvironment(expectations, timeout, (left, token) => log.WaitAsync(loadLine, left, cancellation: token));
```

Repin after a world change. The owned server's startup identity probe is the one read-only bootstrap exception: it checks token, PID and save root while the world loads, then verifies the strict pins before returning an actor.

## Capabilities and extension reload

| Member | Does | Fails when |
|---|---|---|
| `GameActor.RequireCapability(path)` | Discovers a command through `cli_extensions` and returns its `Capability` | The command is missing or its instance token is stale |
| `Invoke(command, arguments)`, `Observe(command, arguments)` | Run a discovered command; `Observe` returns an `Observation` | A stale instance token |
| `ObserveComplete(command, source, arguments)` | Returns a complete observation | `complete` is false: incomplete is never zero. `valheim.world/terrain x z generator|loaded-ground` reports `complete: false` and a null height where no heightmap is loaded |

`ExtensionReload` holds the steps of the [ReloadCheck tool](../../tools/reload-check/README.md), which checks a replacement end to end: new instance, removed command refused, old resources cleaned up, core and connection unchanged.

| Member | Does |
|---|---|
| `Install(artifact, deployed)` | Copies beside the deployed DLL and renames over it |
| `WaitForReplacement(actor, id, version, replacing, timeout, interval)` | Waits for a new live instance at that version |
| `WaitForRemoval(actor, id, timeout, interval)` | Waits until no instance is live |

Both waits re-read `cli_extensions`, which is read-only; a registration announces itself nowhere else.

## Pins

Every run uses strict pins. Each pin protects against a run that passes on something other than what you meant to test.

| Pin | Where | Protects against |
|---|---|---|
| `worlduid` | plan `pins`, checked in game by `cli_expect --strict` before every command | another world, or an unexpected world transition |
| plugin MD5s, `absent` | plan and client `pins`, same check | a stale or different mod build, an extra plugin, a mod that should not be there |
| runtime and world SHA256 manifests | plan `runtime` and `world` | a changed fixture: any file in the runtime or save |
| `game` | `runtimePins`, client `installPins` | another game build: a hotfix, or the `default_old` / `default_pre1_0` branches |
| `loader` | same | another BepInEx, BepInExPack or Doorstop build, or a proxy or configuration that starts the game without BepInEx |
| `patchers` | same | a preloader patcher a removed mod left behind, or another build of one the run needs |

`cli_expect` knows plugins and the world only, so `InstallPins` pins the game build, loader and patchers on disk before launch. Compute them once with `InstallPins.Of(<runtime>)`, review them, and add them to the plan:

```json
"runtimePins": { "game": "...", "loader": "...", "patchers": "..." }
```

An owned client section takes the same object as `"installPins"`. `InstallPins.Check(root, kind)` names what differs and the value found.

Each value is the SHA256 of a listing:

- One line per file, `<sha256>  <relative path>`, `/` separators, ordered by path, as `sha256sum` prints it. `.DS_Store` and AppleDouble `._*` files are left out. An empty or absent folder hashes the empty listing.
- `game` lists every `assembly_*.dll` in the Managed folder (`<executable>_Data/Managed`, `Valheim.app/Contents/Resources/Data/Managed` for the macOS client, or `valheim_server/Data/Managed` for the macOS dedicated server). A build that changes only assets or Unity's own assemblies keeps the same value.
- `loader` lists, relative to the install's root, `winhttp.dll`, `doorstop_config.ini`, `libdoorstop.dylib` and everything under `BepInEx/core` and `doorstop_libs`. `BepInEx/config/BepInEx.cfg` is configuration and not part of it.
- `patchers` lists the whole folder, subfolders included.

Without the library, in the folder concerned:

```sh
find . -type f ! -name .DS_Store ! -name '._*' | sed 's|^\./||' | LC_ALL=C sort | while IFS= read -r f; do printf '%s  %s\n' "$(sha256sum "$f" | cut -d' ' -f1)" "$f"; done | sha256sum
```

For `game`, list `ls assembly_*.dll` in the Managed folder instead of `find`. For `loader`, at the install's root, give `find` the paths `winhttp.dll doorstop_config.ini libdoorstop.dylib BepInEx/core doorstop_libs` instead of `.`, with `2>/dev/null` for those absent.

`FileHash.Sha256` computes these pins, manifests and evidence hashes. `FileHash.Md5` computes plugin pins, which stay MD5 because `cli_manifest` and `cli_expect` report and compare MD5. The runtime manifest also covers the loader files, but a manifest regenerated after an update silently accepts the new build; `runtimePins` makes that a deliberate change.

A link in a pinned folder or loader file is refused, and so is a linked directory on the way to a pinned path (a linked `valheim_Data` or `BepInEx`). The install folder itself may be reached through a link (`/tmp`, `~/.steam/steam`).

## Expected and observed pins

A pin the plan or a campaign dependency lock supplies is **expected**: a mismatch is refused. When neither supplies a value, the profile owner binds what it staged as an **observed** pin. Both are enforced alike, before each boot and by `cli_expect --strict` for the rest of the run. A mixed plan checks its supplied values and observes the rest.

A standalone `PinnedServerRun` stages the server profile and binds:

- the profile's complete runtime manifest, `runtimePins` and startup plugin MD5 set: plugins in `BepInEx/plugins`, plus plugins in `BepInEx/scripts` only when the selected ScriptEngine loads them at startup;
- an omitted world identity, read from the copied fixture, and an omitted world manifest from `WorldFixture.CopyAsFound`. A plan that says `world=any` or `world=<name>` keeps that expectation and needs no existing world UID, so the game may create a new world in an empty fixture.

It refuses a plan that pins a script ScriptEngine does not load at startup, naming the ScriptEngine or `LoadOnStart` setting. After binding, every plugin the runner declares required must be in the staged set: `The staged server profile is missing required plugin ...`. `ValidateServerPlan` accepts omitted standalone pins but validates every supplied manifest, runtime pin and plugin MD5.

The profile replaces the install's own ValheimCLI with the toolkit's set. A supplied pin for that source build is checked before the replacement; a different staged ValheimCLI MD5 is recorded as observed.

| Client | Needs | Observed |
|---|---|---|
| Owned, disposable profile (default) | May omit `installPins` and plugin pins | The source install; the staged profile is the enforced set, every value recorded with its origin. After the run the install is settled ([source checks](sessions-profiles.md#source-checks)) |
| Owned, `inPlace` | `installPins` and an exact ValheimCLI MD5 in `pins` | Nothing; its source verdict is `not applicable` |
| Owned, placed on another host by an inventory | `installPins` | <!-- TODO(docs): what a remote owned client observes --> |
| Attached | Exact plugin expectations checked in game; no `installPins`, its operator's install is not read | Nothing; the game build is not pinned, since the game refuses a join only across network versions |

Use an owned client when the client's build matters. The client fields are on [Game clients in system tests](game-client-session.md).

## Pin origins in the report

`result.json` (schema 5) records `PinOrigins` for standalone runs and owned client profiles. A campaign has already been rewritten to its prepared profiles by `ApplyTo`, so its map is empty.

| Key | For | Value |
|---|---|---|
| `runtimeManifest`, `runtimePins`, `worldManifest`, `worldUid`, `plugins.<guid>` | a standalone run | `plan` or `observed` |
| `<client>.installPins`, `<client>.plugins.<guid>` | an owned client profile | `plan` or `observed` |

A supplied group is `observed` when profile staging replaces it and the enforced value differs, the loader within `runtimePins` included; this is the same rule as a replaced ValheimCLI build.

| Evidence | Records |
|---|---|
| `input-hashes.json` | The standalone map beside the exact runtime pins, plugin pins, world identity and manifests |
| `replacedValheimCliPaths` | The install-relative paths of ValheimCLI files the profile replaced |
| `sourceValheimCliPins` | The checked GUID and MD5, when the plan supplied the replaced source build's pin |
| provenance `pins` = `pins observed, not reviewed` | Added when standalone values were observed: an evidence qualification, while the pins remain enforced |
