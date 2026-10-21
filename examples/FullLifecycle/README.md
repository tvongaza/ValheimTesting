# One mod, every test layer

Test one mod feature at every layer, from a unit test to a real server and client. Copy this example and rename it.

This example takes the mod from [ModWithTests](../ModWithTests/README.md) and tests the same feature at every layer. The unit tests and the scripted integration tests never launch Steam or Valheim. Only the native session test does, and only where you put its private environment files.

**The feature:** the mod adds a server command, `examplemod_mark <x> <z>`, that places a wooden pole (`wood_pole2`) as a marker where the world generator's ground is at least 1.5 m above the sea, and refuses anywhere wetter. The decision is `DrySiteRule`, the very file ModWithTests tests. The marker is an ordinary saved object, so it should persist through a save and restart and appear on clients that do not have the mod.

| Layer | Project | What it shows | Needs |
|---|---|---|---|
| Unit / synthetic | [ModWithTests/MyMod.Tests](../ModWithTests/README.md) | The real decision on declared terrain, with game doubles | .NET 10 SDK |
| Controlled integration | [ExampleMod.Tests](ExampleMod.Tests/) (`MarkerScenarioTests`) | The real scenario and client lifecycle code against scripted game replies: lost replies, save failure, a marker the client cannot see, the declared patch, cleanup | .NET 10 SDK |
| Native system | [ExampleMod.Tests](ExampleMod.Tests/) (`ExampleSession`) with [ExampleMod](ExampleMod/) and [ExampleMod.TestAdapter](ExampleMod.TestAdapter/) | Disposable server and client profiles: check the mod's Harmony patch, join, enforce pins, protect and place the player, measure, save, restart, rejoin, measure again, stop owned processes, and report | A game install, Steam, a machine you may use |

`dotnet run scripts/validate.cs` runs the scripted tests on every platform. The mod and the adapter build only where you have a game install. Set `ValheimPath` (or `VALHEIM_PATH`) to the game folder, and [tools/game-references](../../tools/game-references/README.md) supplies the references or names what is missing. For an independent mod folder, use the [package-only file maps](consumer/README.md).

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

The session protects the joined player (god, ghost and debug mode, read back) as soon as the world is ready, so your scenario never does. Fly stays off.

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
dotnet test examples/FullLifecycle/ExampleMod.Tests/ExampleMod.Tests.csproj -c Release --filter "FullyQualifiedName~DrySiteSessionTests"
```

The native test passes only when the run passed, after the [log scan](../../docs/reference/game-log-scan.md) of every server boot and client. Each run writes a new evidence directory under `session-runs/` in the test output ([what it holds](../../docs/guides/test-save-restart-rejoin.md#check-the-result)).

To include one optional screenshot after the first joined-client marker check, add `reviewStill` to the private plan:

```json
"reviewStill": {
  "hostDirectory": "C:\\pcstage\\example-review-unique-run",
  "gameBuild": "1.0.17",
  "weather": "Clear",
  "timeOfDay": 0.45,
  "cameraDistance": 24,
  "cameraHeight": 12
}
```

Set `hostDirectory` to a fresh, private, absolute path without spaces **on the client host** (a Unix path for a Mac/Linux client). Use a new path for each run and review the fetched `review-marker/example-marker.png` yourself. Set `cameraAzimuthDegrees`, `cameraDistance` and `cameraHeight` in the plan if foliage blocks the view; a successful capture does not prove the marker is visible. Its JSON sidecar is attached to `result.json` with `visualVerdict: "not asserted"`; the test checks capture and restoration, not whether the picture looks good. Omit `reviewStill` for the usual faster run. The scenario passes the campaign client's own `IGameHost` when that client is remote.

## The native session

[Test that a change survives a save, restart and rejoin](../../docs/guides/test-save-restart-rejoin.md) walks through a package-only consumer: generated dependency locks, campaign inputs, feature expectations and the run. The test finds two private files beside the test project, `session.json` and `dry-site-lifecycle.plan.json`, which `.gitignore` lists. Without them, the native test is skipped with that reason, so `dotnet test` stays offline.

[GameSessionFixture.cs](ExampleMod.Tests/GameSessionFixture.cs) runs one session per test class and hands the started session and plan to the tests. Copy it as it is:

```csharp
[Trait("Category", "Native")]
public sealed class DrySiteSessionTests(ExampleSession fixture) : IClassFixture<ExampleSession>
{
    [SessionFact]
    public Task TheDeclaredMarkerScenarioPasses() => fixture.Test(fixture.Plan.Scenario, () => Task.Run(() => MarkerScenario.Run(fixture.Session, fixture.Plan)));
}
```

For simultaneous A/B visibility and B rejoin, select `dry-site-two-clients` with both `SecondClient` and `SecondArrival`. Follow [Run a session with several clients](../../docs/guides/run-a-session-with-several-clients.md); the lifecycle scenario refuses those fields. The xUnit plan filename stays `dry-site-lifecycle.plan.json` for either declared mode.

The server is always owned and runs from a [disposable profile](../../docs/reference/sessions-profiles.md). The client is `owned`, launched from a profile and stopped at the end, or `attach`, which you launch yourself and the run never stops.

### One client hosts the world locally

[`HostedMarkerScenario`](ExampleMod.Tests/HostedMarkerScenario.cs) uses the same mod rule with one client acting as both server and player. Its `HostedRunOptions<HostedMarkerPlan>` passes the hosting client to `PinnedServerRun`; the toolkit's local `HostingClientActor` places the fixture through `HostedWorldOnHost.ThisMachine`, journals it, saves, leaves, rehosts and cleans it up. The scenario asserts the marker before and after rehosting.

From a source checkout, run `dotnet run scripts/bootstrap-cli.cs` before building the test project so its pinned ValheimCLI and BepInExPack are embedded. The local-host profile stages plugins already present in the plan's `client.install`; a pin verifies a DLL but does not install it. Put the built `ExampleMod.dll` and `ExampleMod.TestAdapter.dll` under that install's `BepInEx/plugins` first. Use a disposable modded source install if your Steam install has unrelated plugins or you do not want to modify it. Launch this test from an interactive desktop terminal on Windows; an SSH/session-0 `dotnet test` cannot open a client.

Copy [`sample-hosted-plan.json`](ExampleMod.Tests/sample-hosted-plan.json) to the ignored `ExampleMod.Tests/hosted-dry-site-lifecycle.plan.json`, replace its pins and fixture paths, then run:

```sh
dotnet test examples/FullLifecycle/ExampleMod.Tests/ExampleMod.Tests.csproj -c Release \
  --filter "FullyQualifiedName~HostedMarkerSessionTests"
```

This explicit native test prepares the toolkit's clean `vtseedclean` character, refuses a same-named local or Cloud character, and retires its copy after a passing run has stopped its owned client. On a failed run it keeps the staged save and its registered source: verify the client is stopped before removing them. Use one client at a time with this seed. The run output is `session-runs/hosted-<id>/`, including the collected `host-world`; verify `valheim-test env status` is empty afterwards and the user's `worlds_local` matches its before-run snapshot. Without the private hosted plan, the test is skipped; the default no-game validation excludes it.

## Limits

- The run never signs Steam in or out, and never stops a process it did not start.
- An attached client keeps running, in its menu.
- The toolkit's own [native acceptance suite](../../tests/Valheim.Testing.NativeAcceptance/README.md) runs on a separate mod and is not an example to copy.
