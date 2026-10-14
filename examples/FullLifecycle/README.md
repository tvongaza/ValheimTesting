# One mod, every test layer

Test one mod feature at every layer, from a unit test to a real server and client. Copy this example and rename it.

This example takes the mod from [ModWithTests](../ModWithTests/README.md) and tests the same feature at every layer. The unit tests and the scripted integration tests never launch Steam or Valheim. Only the native session test does, and only where you put its private environment files.

**The feature:** the mod adds a server command, `examplemod_mark <x> <z>`, that places a wooden pole (`wood_pole2`) as a marker where the world generator's ground is at least 1.5 m above the sea, and refuses anywhere wetter. The decision is `DrySiteRule`, the very file ModWithTests tests. The marker is an ordinary saved object, so it should persist through a save and restart and appear on clients that do not have the mod.

| Layer | Project | What it shows | Needs |
|---|---|---|---|
| Unit / synthetic | [ModWithTests/MyMod.Tests](../ModWithTests/README.md) | The real decision on declared terrain, with game doubles | .NET 10 SDK |
| Controlled integration | [ExampleMod.Tests](ExampleMod.Tests/) (`MarkerScenarioTests`) | The real scenario and client lifecycle code against scripted game replies: lost replies, save failure, a marker the client cannot see, the declared patch, cleanup | .NET 10 SDK |
| Native system | [ExampleMod.Tests](ExampleMod.Tests/) (`ExampleSession`) with [ExampleMod](ExampleMod/) and [ExampleMod.TestAdapter](ExampleMod.TestAdapter/) | A disposable server copy and a real client: the mod's Harmony patch applied, join, strict pins, protect and place the player, exercise the feature, measure, confirmed save, restart, rejoin, measure again, stop what it started, report | A game install, Steam, a machine you may use |

`dotnet run scripts/validate.cs` runs the scripted tests on every platform. The mod and the adapter build only where you have a game install. Set `ValheimPath` (or `VALHEIM_PATH`) to the game folder, and [tools/game-references](../../tools/game-references/README.md) supplies the references or names what is missing. Outside this repository, build the test project from the published package with `-p:GameSessionsPackageVersion=<version>` ([package table](../../docs/package-versions.md)).

## What belongs to the mod, and what to the toolkit

| The mod supplies | The toolkit supplies |
|---|---|
| The feature and its command (`ExampleMod/Plugin.cs`) | Server lifecycle: profiles, startup, identity handshake, restart, teardown, report (`PinnedServerRun`, `GameSession`, `ServerActor`) |
| Plan fields and rules (`MarkerPlan.cs`): dry site, wet site, where the player stands | The client section and its rules (`ClientRunPlan`); owned or attached clients (`ClientSession`) |
| Expectations and scenario (`MarkerScenario.cs`) | Join, leave, readiness, protection, placement, strict pins (`GameActor`), rounds (`ClientRounds`) |
| A test adapter serving the owned-session identity (`ExampleMod.TestAdapter`) | Registration and identity capability ([Valheim.Testing.Adapter](../../docs/packages/Valheim.Testing.Adapter.md)) |
| The patch it declares (`MarkerScenario.Patches`) | The [census check](../../docs/reference/game-content-census.md#harmony-census) at runtime-ready (`ModDeclaration`, `HarmonyCensus`) |
| Its tests, scripted and native | Scripted fakes (`ScriptedTransport`, `FakeOwnedProcess`) and the [fixture to copy](ExampleMod.Tests/GameSessionFixture.cs) |

You declare your mod's patch in one line. The run fails at runtime-ready, by name, if it is not applied:

```csharp
public static readonly DeclaredPatch[] Patches = [new("Terminal::InitTerminal", "postfix", "ExampleMod.Plugin+RegisterCommands::Postfix")];
```

Your scenario never protects the player. The session protects the joined player (god, ghost and debug mode, read back) as soon as the world is ready. Fly stays off, so the checks measure a player standing on the ground.

Observations use ValheimCLI's generic commands: `cli_zdos_at` on the server and `cli_prefabs_at` on the client. If your mod needs a test-only action or observation, add it to your adapter as another extension command.

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

<!-- TODO(docs): capture output of `valheim-test session check ... --hosts` -->

The native test passes only when the run passed. After the client and server stop, the run [scans their logs](../../docs/reference/game-log-scan.md); a failure pattern fails the run. Each run writes a new directory under `session-runs/` in the test output:

| File | What it records |
|---|---|
| `result.json`, `junit.xml` | Every step, with its error; log-scan counts in `result.json` |
| Server logs | One log per server boot |
| `connection-N.jsonl`, `client-commands.jsonl` | Every command sent |
| `client-process.json` | The owned client |
| Arrival observations | What the client observed on arrival |

## Prepare the native session

The test finds two private files beside the test project: `session.json` (the session manifest) and `dry-site-lifecycle.plan.json` (the plan). `.gitignore` lists both. If either is missing, the native test is skipped with that reason, so `dotnet test` stays offline.

1. **A server runtime** with BepInEx, the pinned default `valheimCLI.dll`, the mod and the adapter. Its ValheimCLI `[Server] Port` equals the plan's `port`. Keep `BepInEx/patchers` empty. Pin it by hash with `WorldFixture.Manifest`, and its game build, loader and patchers with `InstallPins.Of` in `runtimePins` ([pins](../../docs/reference/game-actors-and-pins.md#pins)). The run refuses a runtime whose game build or loader differs, and runs the server from a disposable loader profile.
2. **A world fixture** that has never been marked, pinned the same way. Pick the sites from its generator heights, for example with the `capture` probe of [ObserveCheck](../ObserveCheck/README.md) on the `generator` layer. They are your expectations; the scenario never takes them from the mod.
3. **A client install** with BepInEx and the pinned default `valheimCLI.dll` only. The mod is pinned `absent`, because the claim is that a client without it sees the marker. Its ValheimCLI port differs from the server's. Its ValheimCLI settings include `AllowOnServerClients = true`, because Valheim 1.0 refuses a joined client's cheat commands. Use an existing, disposable local character, never a Steam Cloud character. Pin the install with `InstallPins.Of` in `client.installPins`.
4. **The plan**: start from [sample-plan.json](ExampleMod.Tests/sample-plan.json), replace every path, hash and coordinate, and save it as `dry-site-lifecycle.plan.json`.
5. **The session manifest** `session.json`: the dedicated server and one client named `client`, the fixture world and a reviewed dependency lock per role. Without an environment inventory, every actor runs on this machine ([GameSessions](../../docs/packages/Valheim.Testing.GameSessions.md)).

The plan carries your mod's fields. These values are illustrative:

```json
"drySite": { "x": 120, "z": -380, "ground": 45.2 },
"wetSite": { "x": 900, "z": 150, "ground": 18.4 },
"arrival": { "x": 125, "z": -380, "ground": 45.0 },
```

## Owned or attached client

- **`owned`**: the run launches the client from `client.install` with a disposable loader profile, and stops only that process when the scenario ends. It refuses before launching if something listens on the client's CLI port, no Steam client is running, or the `passwordVariable` is missing from its environment. Run the test in the desktop session where Steam is running and signed in, with a display.
- **`attach`**: you launch the client and sign in, then run the test. It connects to the client's ValheimCLI port, leaves the world at the end and never touches the process.

The server is always owned. The run copies the fixtures, starts that copy, proves by the adapter's identity handshake that it talks to that process, and stops only it.

## The rounds

The client half is the toolkit's `ClientRounds` ([client rounds](../../docs/reference/game-client-rounds.md)). `MarkerScenario` supplies only what belongs to the mod: the arrival point and step name, the measurement and the check after the restart. The rounds are `first` and `after-restart`, with a confirmed save and a server restart between them:

```csharp
measure: round => round.Step("the client sees the marker at the dry site", () => RequireClientMarkers(round.Client, plan.DrySite, 1)),
afterRestart: round => round.Step("the server still has one marker at the dry site, none at the wet site",
    () => { RequireServerMarkers(round.Server, plan.DrySite, 1); RequireServerMarkers(round.Server, plan.WetSite, 0); }));
```

See the full call in [MarkerScenario.cs](ExampleMod.Tests/MarkerScenario.cs).

## A native session as a test

Copy [GameSessionFixture.cs](ExampleMod.Tests/GameSessionFixture.cs) into your test project. It runs one session per test class, hands the started session and plan to the tests, then lets the run tear down and report. If the run never started or ended with exit code other than 0, the class fails. A failed test also fails the run's `result.json`. [ExampleSession.cs](ExampleMod.Tests/ExampleSession.cs) reads the two private files and names the session's one client:

```csharp
[Trait("Category", "Native")]
public sealed class DrySiteSessionTests(ExampleSession fixture) : IClassFixture<ExampleSession>
{
    [SessionFact]
    public Task TheMarkerSurvivesASaveAndARestart() => fixture.Test("dry-site lifecycle", () => Task.Run(() => MarkerScenario.Run(fixture.Session, fixture.Plan)));
}
```

## Cleanup and its limits

On any failure, including an exception or Ctrl+C, the run stops the owned client, then the owned server, and writes the report. The copies stay in the output directory; the pinned sources never change. The run never signs Steam in or out, never stops a process it did not start, never finds processes by name and never edits the client install's settings. An attached client keeps running, in its menu. Before a native run, read [runtime hygiene](../../docs/runtime-hygiene.md).

The toolkit's own native acceptance scenarios run on a separate mod and are not an example to copy: [native acceptance suite](../../tests/Valheim.Testing.NativeAcceptance/README.md).
