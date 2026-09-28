# Add testing to a mod

Start with the lowest test layer that answers the question. Existing xUnit tests do not need to be rewritten or moved to this repository. Test packages belong in test projects, not in a production mod or a player's plugins directory.

## 1. Build the preview packages without Valheim

Use .NET 10 SDK and Python 3.12 or newer. From a fresh checkout:

```sh
git clone https://github.com/tvongaza/ValheimTesting.git
cd ValheimTesting
python3 scripts/bootstrap-cli.py
python3 scripts/validate.py
```

Bootstrap fetches the exact ValheimCLI commit in [`cli-dependency.json`](../cli-dependency.json); it does not use whichever checkout happens to be nearby. Validation runs library tests, builds all examples, runs the no-game examples, and creates `.packages/`. No Unity, game files, Steam login or running server is needed.

The current local feed contains:

| Package | Exact version | Use |
|---|---|---|
| `Valheim.Testing` | `0.1.0-preview.5` | Composable terrain, zone state and recorded-input replay; no ValheimCLI dependency |
| `Valheim.Testing.Game` | `0.1.0-preview.8` | External game observations, owned sessions, comparisons and reports |
| `Valheim.Cli.Testing` | `0.1.0-preview.4` | ValheimCLI-owned transport, consumed by the Game package |

Versions need not match each other. These previews are built locally, not available from NuGet.org. Add `.packages` alongside NuGet.org, which still supplies xUnit and ordinary dependencies. For example, from your mod checkout:

```sh
dotnet restore path/to/MyMod.Tests.csproj \
  --source /absolute/path/ValheimTesting/.packages \
  --source https://api.nuget.org/v3/index.json
```

Pin only the package your test project needs:

```xml
<!-- Pure test project; not the production mod project. -->
<PackageReference Include="Valheim.Testing" Version="[0.1.0-preview.5]" />
<!-- A separate external system-test project instead uses: -->
<PackageReference Include="Valheim.Testing.Game" Version="[0.1.0-preview.8]" />
```

Brackets mean an exact NuGet version. Pure helpers target netstandard2.0; external game tools target net9.0 and examples can run on .NET 10. Keep the game-side plugin's existing target framework.

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

Wait for the world and required zone to be loaded, arrange arrival/protection separately, and verify the client's actual position. A responsive ValheimCLI is not proof that world loading has finished. Missing maps or incomplete observations are failures, not zero-height or black-paint measurements.

For persistence, use the **same** independently declared plan before and after a confirmed save, server restart and client rejoin. Require `cli_save`'s completion result before stopping. Run a discriminating negative expectation too: unchanged pre-road paint should fail painted samples while untouched samples still pass. Never widen tolerances merely to make a fixture pass.

The [Roads scenario guide](https://github.com/tvongaza/ProceduralRoads/blob/review/testing-adoption-ready/ProceduralRoads.SystemTests/README.md) shows owned process/copy setup, manifests, preparation versus acceptance, empty-save, bridge respawn and native terrain/paint. MWL's [adapter guide](https://github.com/tvongaza/MoreWorldLocations_All/blob/review/testing-adapter-ready/MoreWorldLocations.TestAdapter/README.md) keeps its port probes separate; those require full mode and their gameplay acceptance is still pending.

## 5. Read the result, then keep human judgement separate

Most comparison examples take a new output directory and write `result.json`, `junit.xml`, command transcripts and residuals. Exit 0 establishes only that tool's assertions. The example READMEs describe their outputs and effects. Inspect game logs too; a passing scenario does not certify every loaded mod.

[WalkingReview](../examples/WalkingReview/README.md) records a person moving normally. A qualifying trace still needs a human verdict on usability and appearance. Small cosmetic bumps can be accepted; a test need not demand a perfect road.

Attachment examples never claim or restore the machine and do not own an existing game process. Owned session tools stop only processes they started; their disposable copies and reports remain for inspection. The operator owns station/account coordination, backups, protection and restoration. Review reports for private account/world data before publishing.

For composable slopes, cliffs and terraces plus independent per-zone height/paint state, use the [shared-world guide](shared-world.md). Keep game-type shims and writer assertions in your mod.

## Strict calls from tests

Use `valheim-cli --expect-strict /private/pins.txt ...` when invoking the executable. Its flag is **`--expect-strict`**, not a global `--strict`. YAML plans can instead set `game.expect` and `game.expectStrict: true`. The underlying game command is `cli_expect --strict key=value ...`.

For library consumers, `GameActor.VerifyEnvironment` validates and normalizes supplied expectations to strict mode even if the caller omitted the switch. Every subsequent command rechecks those same pins before dispatch; drift invalidates the actor and blocks the action. `Execute(..., requireSuccess: false)` lets a test inspect an expected command refusal but cannot bypass a pin failure. The transport itself is low-level: use an actor for test actions and observations.

Strict mode rejects unlisted loaded plugins/worlds; it does not make `any` an exact build pin. Supply reviewed full hashes and the intended world identity. The preflight is a separate round trip, not an atomic lock on game state. For dispatch-time enforcement as well, configure the game's existing `[Expectations]` guard; tests never disable it.

Reload tests must provide a pins file and explicitly advance the changed plugin's expected hash using the artifact they intend to install, then require absence on removal. Only `cli_expect --strict` is retried while that known transition settles. Repin after world changes. The owned-server startup identity probe is a narrow read-only bootstrap exception while world loading is incomplete; it checks token/PID/save-root identity, then verifies strict pins before returning an actor.
