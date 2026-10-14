# Valheim.Testing.Game

[Current preview API reference](https://tvongaza.github.io/ValheimTesting/reference/). This page is the package's README; each type's members are in the reference.

`Valheim.Testing.Game` (net10.0) drives the real game from outside it: named actors over ValheimCLI's transport with strict environment pins, event-driven waits, owned server and client sessions it starts and stops, client rounds, typed observations, and JSON/JUnit reports. Use it for the checks that need the game: a save and restart, replication to a client, what a second process registered, what the log says. Keep decisions and formats in unit tests with [`Valheim.Testing`](Valheim.Testing.md) and [`Valheim.Testing.Doubles`](Valheim.Testing.Doubles.md). A run of several game processes, or on another machine, belongs to [`Valheim.Testing.GameSessions`](Valheim.Testing.GameSessions.md), built on this package. A one-command load check is the `valheim-test` tool ([NativeSmoke](Valheim.Testing.NativeSmoke.md)). A mod's game-side half, its test adapter, compiles [`Valheim.Testing.Adapter`](Valheim.Testing.Adapter.md). The transport is [`Valheim.Testing.Cli`](Valheim.Testing.Cli.md), a dependency of this package.

## Install

Add the package to a separate system-test project, never to the mod. It targets net10.0.

```xml
<PackageReference Include="Valheim.Testing.Game" Version="[0.1.0-rc.1]" />
<PackageReference Include="Valheim.Testing.GameSessions" Version="[0.1.0-rc.1]" />
```

Brackets pin an exact version; the released versions are in the [package table](../package-versions.md).

## On one Windows PC

The usual setup is one Windows PC with Valheim installed through Steam: no inventory file, SSH or second host.

1. **Install** Valheim and the free **Valheim Dedicated Server** tool (Steam app 896660) through Steam. Keep the Steam client running and signed in; an owned client needs it, the dedicated server does not.
2. **Use disposable test profiles, never the Steam folders themselves.** A server runtime needs BepInExPack_Valheim, the default `valheimCLI.dll`, your mod and its [test adapter](Valheim.Testing.Adapter.md). A client install needs BepInEx and the same plugin, and your mod when the client should have it. Give each its own ValheimCLI port, a disposable world and a disposable local character.
3. **Run a check.** Once both installs have BepInExPack_Valheim, one command checks that a mod loads on a dedicated server and that a clean client joins it:

```sh
valheim-test server-load --mod YourMod.dll
```

The run passes when the server loads the mod and the client joins ([Check that a mod loads](../guides/check-a-mod-loads.md)). The machine's Steam installs, ports and folders are detected; `valheim-test env list` prints what it found and what it assumed ([this machine, with no file](../reference/game-launch.md#this-machine-with-no-file)). Servers on Linux or macOS, and actors on other machines, are optional: start there only when one PC is not enough ([Platforms](../reference/platforms.md), [GameSessions](Valheim.Testing.GameSessions.md)).

## What a test runtime holds

A clean test runtime is BepInEx core plus the plugins below, with an empty `BepInEx/patchers` folder. A preloader patcher a removed mod left behind breaks the game's types before any plugin loads, and the pinned runner refuses a patchers folder that is not the pinned one, naming what it holds. Install one pinned `valheimCLI.dll` in `plugins`; the [release contract](../maintainers/valheimcli-release-contract.md) identifies the matching build.

| Component | Dedicated server | Client |
|---|---|---|
| Default ValheimCLI plugin | Yes, in `plugins`: save, session and world commands | Yes, in `plugins`: character, terrain, observation, reflection and capture commands |
| Your mod and its test adapter | Yes | Yes, or pinned `absent` when the claim is that a client without the mod works |
| ScriptEngine | Only for reload tests | Only for reload tests |

Strict pins name every loaded plugin by MD5 and the world by UID, one `key=value` per line. An illustrative client file, not usable pins:

```text
worlduid=YOUR_FIXTURE_WORLD_UID
valheimCLI.valheimCLI=ACTUAL_CORE_MD5
example.mymod=absent
```

List every other loaded plugin too. On the server, pin the mod and its adapter by their real MD5s. `cli_expect` cannot see the game build or BepInEx, so the runner pins those on disk ([Pins](../reference/game-actors-and-pins.md#pins)).

## A first native check

Copy the [FullLifecycle example](../../examples/FullLifecycle/README.md)'s native session. Put its private `session.json` and plan beside the test project ([prepare the native session](../../examples/FullLifecycle/README.md#prepare-the-native-session)), then run from a ValheimTesting checkout:

```sh
valheim-test session check examples/FullLifecycle/ExampleMod.Tests/session.json --hosts
dotnet test examples/FullLifecycle/ExampleMod.Tests/ExampleMod.Tests.csproj -c Release --filter "Category=Native"
```

Without those files the native test is skipped with that reason. When it runs, it:

1. starts an owned dedicated server from a disposable profile of the runtime, with a fresh copy of the world, and an owned client;
2. joins, verifies strict pins, protects the player and places it beside the site;
3. measures: the client sees the marker at the dry site;
4. saves with confirmation, restarts only the server, rejoins and measures again;
5. stops both processes, scans their logs and writes `result.json` and `junit.xml` under `session-runs/` in the test output.

Check the same independently declared expectations before and after the save, the restart and the rejoin, run a negative expectation that would fail, and never widen a tolerance to make a fixture pass.

## Where each behaviour is documented

| Topic | Page |
|---|---|
| Actors, strict pins, fixtures, reports, pin origins | [Actors and pins](../reference/game-actors-and-pins.md) |
| Waits, startup events, owned server session, stopping | [Waiting and owned processes](../reference/game-waiting.md) |
| Log patterns and the teardown scan | [Log scan at teardown](../reference/game-log-scan.md) |
| Server and client launch, this machine's inventory | [Launching servers and clients](../reference/game-launch.md) |
| `ClientRunPlan`, `ClientSession` and their evidence | [Game clients in system tests](../reference/game-client-session.md) |
| Static checks before a run, the ValheimCLI manifest | [Owned-run preflight](../reference/game-preflight.md) |
| Join, protect, place and arrive | [Joining, protecting and placing a player](../reference/game-player-placement.md) |
| Refused joins, crossplay, hosted worlds | [Refused joins, crossplay and hosted worlds](../reference/game-joins.md) |
| Plan rules, `ClientRounds`, zone and logout cycles | [Plan rules and client rounds](../reference/game-client-rounds.md) |
| Terrain probes, global keys, synced config, vanilla clients, dungeons | [World observations](../reference/game-observations.md) |
| Content census and Harmony census | [Content and Harmony census](../reference/game-content-census.md) |
| Targeted regressions, loader packages, dependency locks | [Targeted regressions, loader packages and dependencies](../reference/game-run-inputs.md) |
| Evidence in `result.json`, snapshots, review stills | [Evidence](../reference/evidence.md) |
| Windows, Linux, macOS | [Platforms](../reference/platforms.md) |
| A one-command load check | [Check that a mod loads](../guides/check-a-mod-loads.md) |
| Pinned world dumps | [Pinned world dumps](../reference/world-dump-contract.md) |
| Baking a saved fixture | [Bake a fixture](../reference/fixture-authoring.md) |
| Several processes or machines as one run | [Valheim.Testing.GameSessions](Valheim.Testing.GameSessions.md) |
| Checklist before a native run | [Before a native run](../guides/before-a-native-run.md) |

## Test fakes

`Valheim.Testing.Game.Fakes` tests scenario and session code without a game.

| Fake | Use it to |
|---|---|
| `ScriptedTransport` | Answer strict pins, the extension listing, extension commands, confirmed saves and scripted commands; throws on anything unscripted, records every command; `Actor()` returns a pinned `GameActor` |
| `FakeOwnedProcess` | Stand in for an owned server or client: `Exit(code)` ends it, `Exited(code)` crashed at startup, `IgnoreQuit` turns a clean stop into a kill, `StopFailure` makes every stop throw |
| `FakeOwnedServer` | Launch `FakeOwnedProcess`es and answer the session probe and pins; switches make each startup failure happen, `Events` records the lifecycle |
| `FakeClientDataDirectory` | Point `HostedWorld.DefaultSaveDirectory` at a directory on every platform. No-game tests only; nested scopes restore the outer one on dispose, innermost first |
| `FakeDataRoot` | Move ValheimTesting's own folder (journal, host lock, leases, `CliBundle.DataRoot`) to a directory, so test copies never show in `valheim-test env status`. No-game tests only; nested scopes restore innermost first; disposing out of order throws |
| `TempRuntime` | A runtime directory whose BepInEx log a test appends to or rewrites, as each boot does |

## Limits

- A successful fake-transport run is an orchestration test, not an in-game pass.
- An input replay round-trip proves capture plumbing, not the game's terrain implementation.
- Output may contain player and world data; review it before publishing.
- Never put credentials in fixture configuration or logs.
