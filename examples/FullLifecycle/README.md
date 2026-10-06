# One mod, every test layer

**Audience:** a mod author taking one feature into the real game. **Concept:** the same feature tested at every layer, each in its own project. Copy it and rename it.

This example takes the mod from [ModWithTests](../ModWithTests/README.md) and tests the same feature at every layer, from a unit test to a real server and client. Running the unit tests and the scripted integration tests never launches Steam or Valheim; only the native session test does, and only where you put its private environment files.

**The feature:** the mod adds a server command, `examplemod_mark <x> <z>`, that places a wooden pole (`wood_pole2`) as a marker where the world generator's ground is at least 1.5 m above the sea, and refuses anywhere wetter. The decision is `DrySiteRule`, the very file ModWithTests tests. The marker is an ordinary saved object, so it should persist through a save and restart and appear on clients that do not have the mod.

| Layer | Project | What it shows | Needs |
|---|---|---|---|
| Unit / synthetic | [ModWithTests/MyMod.Tests](../ModWithTests/README.md) | The real decision on declared terrain, with game doubles | .NET 10 SDK |
| Controlled integration | [ExampleMod.Tests](ExampleMod.Tests/) (`MarkerScenarioTests`) | The real scenario and client lifecycle code against scripted game replies: lost replies, save failure, a marker the client cannot see, the declared patch, cleanup | .NET 10 SDK |
| Native system | [ExampleMod.Tests](ExampleMod.Tests/) (`ExampleSession`) with [ExampleMod](ExampleMod/) and [ExampleMod.TestAdapter](ExampleMod.TestAdapter/) | A disposable server copy and a real client: the mod's Harmony patch applied, join, strict pins, protect and place the player, exercise the feature, measure, confirmed save, restart, rejoin, measure again, stop what it started, report | A game install, Steam, a machine you may use |

`dotnet run scripts/validate.cs` runs the scripted tests on every platform. The mod and the adapter compile against your game install, so they build only where you have one. They take its references from [tools/game-references](../../tools/game-references/README.md): set `ValheimPath` (or `VALHEIM_PATH`) to the game folder, and a missing install, BepInEx or ValheimCLI core stops the build with an error naming it. Outside this repository, build the test project from the published package with `-p:GameSessionsPackageVersion=<version>` ([package table](../../docs/getting-started.md#package-versions-and-feeds)).

## What belongs to the mod, and what to the toolkit

| The mod supplies | The toolkit supplies |
|---|---|
| The feature and its command (`ExampleMod/Plugin.cs`) | The owned server lifecycle: copies, startup events, identity handshake, restart, teardown, report (`PinnedServerRun`, `GameSession`, `ServerActor`) |
| The plan fields and their rules (`MarkerPlan.cs`): which site is dry, which is wet, where the player stands | The client section and its rules (`ClientRunPlan`), and owned or attached clients (`ClientSession`) |
| The expectations and the scenario (`MarkerScenario.cs`): one marker here, none there, still there after a restart | Session steps (join, leave, readiness, protection once the world is ready), player placement, strict pins (`GameActor`), the client rounds (`ClientRounds`) |
| A test adapter serving the owned-session identity and the Harmony census (`ExampleMod.TestAdapter`) | The adapter's registration, identity capability and census command ([Valheim.Testing.Adapter](../../docs/packages/Valheim.Testing.Adapter.md), compiled into the adapter) |
| The patch it declares (`MarkerScenario.Patches`) | The census check at runtime-ready (`ModDeclaration`, `HarmonyCensus`): each declared patch applied, other owners on the same methods reported |
| Its tests, scripted and native | The scripted fakes (`ScriptedTransport`, `FakeOwnedProcess`) and the session fixture to copy ([GameSessionFixture](ExampleMod.Tests/GameSessionFixture.cs)) |

The scenario never protects the player itself: the session protects the joined player (god, ghost and debug mode, read back) as soon as the world is ready, so the join step fails if the game does not confirm it, and the player is never moved unprotected. Fly stays off; the arrival and marker checks measure a player standing on the ground.

Observations use ValheimCLI's generic commands (`cli_zdos_at` on the server, `cli_prefabs_at` on the client). A mod that needs a test-only action or observation adds it to its adapter as another extension command; the adapter package has many ready ([Valheim.Testing.Adapter](../../docs/packages/Valheim.Testing.Adapter.md)).

## Run the layers

```sh
# Unit and scripted integration: no game.
dotnet test examples/ModWithTests/MyMod.Tests/MyMod.Tests.csproj -c Release
dotnet test examples/FullLifecycle/ExampleMod.Tests/ExampleMod.Tests.csproj -c Release --filter "Category!=Native"

# Game-side projects, against your install and the ValheimCLI core in the test runtime:
dotnet build examples/FullLifecycle/ExampleMod/ExampleMod.csproj -c Release -p:ValheimPath="<install>"
dotnet build examples/FullLifecycle/ExampleMod.TestAdapter/ExampleMod.TestAdapter.csproj -c Release -p:ValheimPath="<install>" -p:CliDll="<runtime>/BepInEx/plugins/valheimCLI.dll"

# The native session: check its environment without launching anything, then run the native test.
valheim-test session check examples/FullLifecycle/ExampleMod.Tests/session.json --hosts
dotnet test examples/FullLifecycle/ExampleMod.Tests/ExampleMod.Tests.csproj -c Release --filter "Category=Native"
```

The native test passes only when the run passed. After the client and server stop, the run scans their logs (the server's per boot, the owned client's BepInEx log and `Player.log`) for known problems such as a Harmony patch on a method that does not exist; a failure pattern fails the run, and every count is in `result.json` ([log scan](../../docs/packages/Valheim.Testing.Game.md#log-scan-at-teardown)). Every run writes a new directory under `session-runs/` in the test output with `result.json` and `junit.xml` (every step, with its error), per-boot server logs, `connection-N.jsonl` and `client-commands.jsonl` (every command sent), `client-process.json` for an owned client, and the arrival observations.

## Prepare the native session

The test finds its environment by convention, with no setting: `session.json` beside the test project is the session manifest, and `dry-site-lifecycle.plan.json` beside it is the plan. Both are read in place, are private and are never committed (`.gitignore` lists them). Without them the native test is skipped with that reason, so `dotnet test` stays offline by default.

1. **A server runtime** with BepInEx, ValheimCLI (core and the Standard and WorldTools packs), the mod and the adapter; its ValheimCLI `[Server] Port` equals the plan's `port`. Keep it clean: BepInEx core plus these plugins, with an empty `BepInEx/patchers` folder (the `patchers` pin refuses a leftover patcher, in the runtime and in an owned client's install). Pin it by hash with `WorldFixture.Manifest`, and its game build, loader and patchers with `InstallPins.Of` in `runtimePins`; the run works on a copy and refuses one whose game build or loader differs ([what each pin protects against](../../docs/packages/Valheim.Testing.Game.md#pins-and-the-opt-out)).
2. **A world fixture** that has never been marked, pinned the same way. Pick the sites from its generator heights (for example with [ObserveCheck](../ObserveCheck/README.md)'s `capture` probe on the `generator` layer) and write them into the plan. They are your expectations: the scenario never takes them from the mod.
3. **A client install** with BepInEx and ValheimCLI (core and the Standard pack) only: the mod is pinned `absent`, because the claim is that a client without it sees the marker. Its ValheimCLI port differs from the server's, and its ValheimCLI settings include `AllowOnServerClients = true`: Valheim 1.0 refuses a joined client's cheat commands whatever the server's admin list says, and this ValheimCLI opt-in is what lets its test commands (protection, object listing) run there. It needs an existing, disposable local character; never use a Steam Cloud character. Pin the install's game build, loader and patchers with `InstallPins.Of` in `client.installPins`: Steam updates the game on its own, and the run refuses an install that no longer matches. The run never launches this install: it copies it first, stages the toolkit's pinned ValheimCLI set into the copy and launches the copy ([GameSessions](../../docs/packages/Valheim.Testing.GameSessions.md#an-owned-client-on-this-machine-runs-from-a-disposable-copy)).
4. **The plan**: start from [sample-plan.json](ExampleMod.Tests/sample-plan.json), replace every path, hash and coordinate, and save it beside the test project as `dry-site-lifecycle.plan.json`. This example runs with strict pins only: its scenario joins and checks by the pinned world uid, so it refuses `"pinning": "none"`.
5. **The session manifest** `session.json`: the dedicated server and one client named `client` (the plan's client section), the fixture world and a reviewed dependency lock per role; for actors on other machines, the private environment inventory that places them ([a campaign](../../docs/packages/Valheim.Testing.GameSessions.md#a-campaign-remote-clients-and-steam-identities)). Left without an inventory, every actor runs on this machine.

## Owned or attached client

- **`owned`**: the run launches the client (a disposable copy of `client.install`) and stops that process, and only that process, when the scenario ends, whether it passed or failed. It refuses before launching if something already listens on the client's CLI port, if no Steam client is running, or if the join password variable is missing from its own environment (the client inherits it). Run the test **in the desktop session where Steam is running and signed in**, with a display: a client started from a service, a scheduled task without a desktop, or a plain SSH session cannot open a window or reach Steam.
- **`attach`**: you launch the client and sign in yourself, then run the test; it connects to the client's ValheimCLI port, leaves the world at the end and never touches the process.

Either way the server is always owned: the run copies the fixtures, starts that copy, proves by the adapter's identity handshake that it is talking to the process it started, and stops only that process.

## The rounds

The client half of the scenario is the toolkit's `ClientRounds` (see [Plan rules and client rounds](../../docs/packages/Valheim.Testing.Game.md#plan-rules-and-client-rounds)). `MarkerScenario` supplies only what is this mod's: the arrival point and the name of its step (`arrive beside the marker`), the measurement (the client sees the marker at the dry site), and the check after the restart (the server still has one marker at the dry site and none at the wet site). The helper waits until the server accepts connections, then joins, protects and arrives, and runs the measurement. Between the two rounds (`first`, `after-restart`) it saves with confirmation, has the client leave and restarts only the owned server; after the last round the client leaves. It closes the client in every outcome.

## A native session as a test

[GameSessionFixture](ExampleMod.Tests/GameSessionFixture.cs) is a copyable `IAsyncLifetime` adapter (copy the file into your test project; one session per test class): it runs the toolkit's runner up to the scenario, hands the started session and plan to the test, and lets the run tear down and report when the class finishes. A run that never started, or ended with a failed step or cleanup, fails the class with its exit code, and a failed test fails the run's `result.json` too. [ExampleSession](ExampleMod.Tests/ExampleSession.cs) is this mod's fixture: it reads the two files above and names the session's one client. The test carries the trait `Category=Native`, so a filter keeps it out of a CI run with no game.

## Cleanup and its limits

On any failure, including a failed step, an exception or Ctrl+C, the run still stops the owned client, then the owned server, and writes the report. The copies stay in the output directory for inspection; the pinned sources are never changed. It never signs Steam in, out or switches accounts, never stops a process it did not start, never finds processes by name, and never edits the client install's settings. Claim the machine for yourself while a run is going: two runs sharing one install, one port or one Steam account interfere. An attached client keeps running, in its menu.

## The toolkit's own acceptance suite

This repository's native acceptance scenarios (the dry-site lifecycle and its server half, the world lifecycle, a client without the mod, synced config, a refused join, crossplay, the content census, review stills, area objects, two-client ownership and setup, a hosted world, adapter unload), their control plugins and probes run on the suite's own mod, AcceptanceMod, from [tests/Valheim.Testing.NativeAcceptance](../../tests/Valheim.Testing.NativeAcceptance/README.md). It is not an example to copy.
