# One mod, every test layer

This example takes the mod from [ModWithTests](../ModWithTests/README.md) and tests the same feature at every layer, from a unit test to a real server and client, with a separate project per layer. Running the unit or integration tests never launches Steam or Valheim; only the system-test runner does, and only when you ask it to.

**The feature:** the mod adds a server command, `mymod_mark <x> <z>`, that places a wooden pole (`wood_pole2`) as a marker where the world generator's ground is at least 1.5 m above the sea, and refuses anywhere wetter. The decision is `DrySiteRule`, the very file ModWithTests tests. The marker is an ordinary saved object, so it should persist through a save and restart and appear on clients that do not have the mod.

| Layer | Project | What it shows | Needs |
|---|---|---|---|
| Unit / synthetic | [ModWithTests/MyMod.Tests](../ModWithTests/README.md) | The real decision on declared terrain, with game doubles | .NET 10 SDK |
| Controlled integration | [MyMod.IntegrationTests](MyMod.IntegrationTests/) | The real scenario and client lifecycle code against scripted game replies: startup failure, incomplete observations, lost replies, save failure, cleanup | .NET 10 SDK |
| Native system | [MyMod.SystemTests](MyMod.SystemTests/) with [MyMod](MyMod/) and [MyMod.TestAdapter](MyMod.TestAdapter/) | A disposable server copy and a real client: join, strict pins, protect (by default) and place the player, exercise the feature, measure, confirmed save, restart, rejoin, measure again, stop what it started, report | A game install, Steam, a machine you may use |
| Human review | `review` in the plan | An optional look by a person, recorded beside the automated result and never part of it | A person |

`dotnet run scripts/validate.cs` runs the integration tests and builds the runner on every platform. The mod and the adapter compile against your game install, so they build only where you have one.

## What belongs to the mod, and what to the toolkit

| The mod supplies | The toolkit supplies |
|---|---|
| The feature and its command (`MyMod/Plugin.cs`) | The owned server lifecycle: copies, startup events, identity handshake, restart, teardown, report (`PinnedServerRun`, `OwnedServerSession`) |
| The plan fields and their rules (`LifecyclePlan.cs`): which site is dry, which is wet, where the player stands | The client section and its rules (`ClientRunPlan`), and owned or attached clients (`ClientSession`) |
| The expectations and the scenario (`DrySiteScenario.cs`): one marker here, none there, still there after a restart | Session steps (`SessionControl`: devcommands, join, leave, readiness, protection once the world is ready), player placement (`PlayerPlacement`: intro, arrival, support), strict pins (`GameActor`) |
| A test adapter serving the owned-session identity (`MyMod.TestAdapter`) | The adapter's registration and identity capability ([Valheim.Testing.Adapter](../../src/Valheim.Testing.Adapter/Adapter/TestExtension.cs), compiled into the adapter) |
| Integration tests of its scenario | The scripted fakes they use (`ScriptedTransport`) |

The scenario never calls `PlayerPlacement.Protect` itself: `SessionControl.WaitForWorld` protects the joined player (god, ghost and debug mode, read back) as soon as the world is ready, so the join step fails if the game does not confirm it, and the player is never moved unprotected. Fly stays off; the arrival and marker checks measure a player standing on the ground.

Observations use ValheimCLI's generic commands (`cli_zdos_at` on the server, `cli_prefabs_at` on the client). A mod that needs a test-only action or observation adds it to its adapter as another extension command.

## Run the layers

```sh
# Unit and integration: no game.
dotnet test examples/ModWithTests/MyMod.Tests/MyMod.Tests.csproj -c Release
dotnet test examples/FullLifecycle/MyMod.IntegrationTests/MyMod.IntegrationTests.csproj -c Release

# Game-side projects, against your install and the ValheimCLI core in the test runtime:
dotnet build examples/FullLifecycle/MyMod/MyMod.csproj -c Release -p:ValheimManaged="<install>/valheim_Data/Managed" -p:BepInExCore="<install>/BepInEx/core"
dotnet build examples/FullLifecycle/MyMod.TestAdapter/MyMod.TestAdapter.csproj -c Release -p:ValheimManaged="<install>/valheim_Data/Managed" -p:BepInExCore="<install>/BepInEx/core" -p:CliDll="<runtime>/BepInEx/plugins/valheimCLI.dll"

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

- **`owned`**: the runner launches the client from `client.install` and stops that process, and only that process, when the scenario ends, whether it passed or failed. It refuses before launching if something already listens on the client's CLI port, if no Steam client is running, or if the join password variable is missing from its own environment (the client inherits it). The runner must run **in the desktop session where Steam is running and signed in**, with a display: a client started from a service, a scheduled task without a desktop, or a plain SSH session cannot open a window or reach Steam.
- **`attach`**: you launch the client and sign in yourself, then run the runner; it connects to the client's ValheimCLI port, leaves the world at the end and never touches the process. Use it when the runner cannot run in the client's desktop session, or when you want to watch or keep the client.

Either way the server is always owned: the runner copies the fixtures, starts that copy, proves by the adapter's identity handshake that it is talking to the process it started, and stops only that process.

## Cleanup and its limits

On any failure, including a failed step, an exception or Ctrl+C, the runner still stops the owned client, then the owned server, and writes the report. The copies stay in the output directory for inspection; the pinned sources are never changed.

What the runner does not and cannot clean up:

- **Steam**: it never signs in, out or switches accounts, and it cannot tell whether Steam is signed in, only whether it runs. Log-in and Steam Guard are yours; use an account you can dedicate to testing.
- **The machine**: it never stops a process it did not start, never finds processes by name, and never edits the client install's settings. Your character's save changes as it would in play (the first spawn, the position). Claim the machine for yourself while a run is going: two runs sharing one install, one port or one Steam account interfere.
- **An attached client** keeps running, in its menu.

## Human review

With `"review": { "enabled": true, "seconds": 600 }`, after the automated checks of the second round the runner leaves the player beside the marker, prints `REVIEW:` with what to look at, and waits for you to write `review.json` in the output directory: `{"verdict":"pass","notes":"..."}`. The verdict is recorded in `result.json` as `humanReview`; it never adds a step, so the automated result cannot change because of it, and a missing verdict is recorded as such. Walking and appearance are for people to judge; the runner judges only what it measured.
