# Valheim.Testing.Game

[API reference](https://tvongaza.github.io/ValheimTesting/reference/). This page is the package's README; each type's members are in the reference.

`Valheim.Testing.Game` drives the real game from a test: it launches and stops dedicated servers and clients, talks to them through ValheimCLI with strict pins, waits on game events, and writes JSON and JUnit reports. Use it for checks that need the game, such as a save and restart, replication to a client or what the log says.

| You want | Use |
|---|---|
| Logic and formats without the game | [`Valheim.Testing`](Valheim.Testing.md) and [`Valheim.Testing.Doubles`](Valheim.Testing.Doubles.md) |
| A one-command load check | The `valheim-test` tool ([NativeSmoke](Valheim.Testing.NativeSmoke.md)) |
| Several game processes, or another machine | [`Valheim.Testing.GameSessions`](Valheim.Testing.GameSessions.md), built on this package |
| Your mod's game-side test adapter | [`Valheim.Testing.Adapter`](Valheim.Testing.Adapter.md) |

The transport, [`Valheim.Testing.Cli`](Valheim.Testing.Cli.md), comes with this package.

## Install

Add the package to a separate system-test project, never to the mod. It targets net10.0.

```xml
<PackageReference Include="Valheim.Testing.Game" Version="[0.3.0]" />
<PackageReference Include="Valheim.Testing.GameSessions" Version="[0.3.0]" />
```

Brackets pin an exact version; the released versions are in the [package table](../package-versions.md).

## On one Windows PC

Native checks run on the Windows PC where Steam installed Valheim and the free Valheim Dedicated Server (Steam app 896660). Keep the Steam client running and signed in for a client. You write no inventory file: the toolkit finds the installs, ports and folders ([this machine's inventory](../reference/game-inventory.md)).

```sh
valheim-test server-load --mod YourMod.dll
```

The run passes when the server loads the mod and a clean client joins it. It runs both from disposable profiles and leaves the Steam installs untouched. [Your first server load check](../tutorials/first-server-load-check.md) walks through it. Servers on Linux or macOS and hosts on other machines are optional ([Platforms](../reference/platforms.md)).

## What a test runtime holds

A clean test runtime is BepInEx core and the plugins below, with an empty `BepInEx/patchers` folder. The runners build it in a disposable profile: the pinned BepInEx loader on Windows and Linux, the pinned ValheimCLI build, your mod and its adapter. A preloader patcher breaks the game's types before any plugin loads, so the `patchers` pin names whatever that folder holds ([Pins](../reference/game-actors-and-pins.md#pins)).

| Component | Dedicated server | Client |
|---|---|---|
| ValheimCLI (`valheimCLI.dll` in `plugins`) | Yes: save, session and world commands | Yes: character, terrain, observation and capture commands |
| Your mod and its test adapter | Yes | Yes, or pinned `absent` when the claim is that a client without the mod works |
| ScriptEngine | Only for reload tests | Only for reload tests |

Strict pins name every loaded plugin by MD5 and the world by UID, one `key=value` per line. An illustrative client file, not usable pins:

```text
worlduid=YOUR_FIXTURE_WORLD_UID
valheimCLI.valheimCLI=ACTUAL_CORE_MD5
example.mymod=absent
```

`cli_expect` cannot see the game build or BepInEx, so the runner pins those on disk.

## A first native check

Copy the [FullLifecycle example](../../examples/FullLifecycle/README.md)'s native session. Put its private `session.json` and plan beside the test project ([the session manifest](../reference/sessions-campaigns.md#the-session-manifest)), then run from a ValheimTesting checkout:

```sh
valheim-test session check examples/FullLifecycle/ExampleMod.Tests/session.json --hosts
dotnet test examples/FullLifecycle/ExampleMod.Tests/ExampleMod.Tests.csproj -c Release --filter "Category=Native"
```

Without those files the native test is skipped with that reason. When it runs, it starts an owned server and client from disposable profiles, joins, checks the marker at the dry site, saves, restarts the server, rejoins, checks again, and writes `result.json` and `junit.xml` under `session-runs/` in the test output.

## Reference pages

| Types | Page |
|---|---|
| `GameActor`, `InstallPins`, `WorldFixture`, `ScenarioReport` | [Actors and pins](../reference/game-actors-and-pins.md) |
| `LogWait`, `StateWait`, `ObservedWait`, `StartupEvents` | [Waiting](../reference/game-waiting.md) |
| `EnvironmentInventory`, `valheim-test env list` and `env preflight` | [This machine's inventory](../reference/game-inventory.md) |
| `GameLaunch` | [Launching servers and clients](../reference/game-launch.md) |
| `OwnedServerSession`, `DirectServerProcess`, `RecordingTransport`, `ProcessStop` | [Owned server session](../reference/game-server-session.md) |
| `ClientRunPlan`, `ClientSession` | [Game clients in system tests](../reference/game-client-session.md) |
| `ClientRunPlan.Preflight`, `CliCapabilityManifest` | [Owned-run preflight](../reference/game-preflight.md) |
| `PlayerPlacement`, `TestAccess` | [Joining, protecting and placing a player](../reference/game-player-placement.md) |
| `SessionControl`, `CrossplayServer`, `HostedWorld` | [Refused joins, crossplay and hosted worlds](../reference/game-joins.md) |
| `ClientRounds`, `ZoneCycle`, `LogoutCycle` | [Plan rules and client rounds](../reference/game-client-rounds.md) |
| `TerrainProbe`, `GlobalKeyFixture`, `SyncedConfig`, `DungeonRooms` | [World observations](../reference/game-observations.md) |
| `ContentCensus`, `HarmonyCensus` | [Content and Harmony census](../reference/game-content-census.md) |
| `LogScanner` | [Log scan at teardown](../reference/game-log-scan.md) |
| `RegressionInputs`, `BepInExLoaderPackage`, `NativeDependencyResolver` | [Loader packages and dependencies](../reference/game-run-inputs.md) |
| `result.json`, snapshots, review stills | [Evidence](../reference/evidence.md) |

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
