# One mod, every test layer

Test the same marker feature as a decision, a scripted scenario and a native gameplay assertion. Start from the complete [standalone file maps](consumer/README.md); your mod needs no toolkit checkout.

`examplemod_mark <x> <z>` places `wood_pole2` on dry generator ground and refuses wet ground. `DrySiteRule` is the same production file tested by [ModWithTests](../ModWithTests/README.md).

| You want to prove | Follow | Source to adapt |
|---|---|---|
| The placement decision is correct | [Get started](../../docs/getting-started.md) | [DrySiteRule](../ModWithTests/MyMod/DrySiteRule.cs) |
| Driver failures are detected without a game | [Scripted controls](consumer/README.md#scripted-lifecycle-tests) | [MarkerScenarioTests](ExampleMod.Tests/MarkerScenarioTests.cs) |
| A marker survives save/restart/rejoin | [Persistence guide](../../docs/guides/test-save-restart-rejoin.md) | [MarkerScenario](ExampleMod.Tests/MarkerScenario.cs) |
| Two clients independently see the marker | [Multi-client guide](../../docs/guides/run-a-session-with-several-clients.md) | The same marker scenario |

## What belongs to the mod, and what to the toolkit

Adapt [MarkerPlan](ExampleMod.Tests/MarkerPlan.cs) with independently measured fixture expectations and [MarkerScenario](ExampleMod.Tests/MarkerScenario.cs) with your actions and assertions. The [adapter](ExampleMod.TestAdapter/Plugin.cs) identifies the owned mod session. [Game sessions](../../docs/reference/sessions-game-session.md) describes the runner's actors, profiles and cleanup.

The [Harmony census](../../docs/reference/game-content-census.md#harmony-census) checks the declared production patch. Generic ValheimCLI observations count server objects and client prefabs independently of the mod's reply.

## Run the layers

Follow the task links above for setup, commands and captured results. The [consumer map](consumer/README.md) includes offline failure controls and the package-only native entry. To validate changes to this repository, follow [CONTRIBUTING.md](../../CONTRIBUTING.md#set-up-and-validate-locally).

### Optional review still

This advanced option adds human-review evidence to the one-client persistence mode.

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

The [persistence guide](../../docs/guides/test-save-restart-rejoin.md) uses the thin package-only entry. To use xUnit instead, copy the complete [scripted/native test map](consumer/README.md#scripted-lifecycle-tests). This checkout's native test instead finds two private files beside the test project, `session.json` ([the session manifest](../../docs/reference/sessions-campaigns.md#the-session-manifest), with one [dependency lock](../../docs/reference/game-run-inputs.md#resolve-mod-dependencies) per role) and `dry-site-lifecycle.plan.json`, which `.gitignore` lists. Without them, the native test is skipped with that reason, so `dotnet test` stays offline; each run writes its [evidence](../../docs/reference/evidence.md) under `session-runs/` in the test output.

The xUnit entry reads private `session.json` and `dry-site-lifecycle.plan.json` beside the test project. Without them, it skips the native test. [ExampleSession](ExampleMod.Tests/ExampleSession.cs) reads the declared mode from that plan; the filename remains the same for either mode.

[GameSessionFixture](ExampleMod.Tests/GameSessionFixture.cs) hands the prepared session to the test class and lets the runner complete teardown afterwards. Copy it unchanged. Use the exact native filter from the [consumer instructions](consumer/README.md#scripted-lifecycle-tests); `Category=Native` also selects the separate hosted test below.

### One client hosts the world locally

[`HostedMarkerScenario`](ExampleMod.Tests/HostedMarkerScenario.cs) uses the same mod rule with one client acting as both server and player. Its `HostedRunOptions<HostedMarkerPlan>` passes the hosting client to `PinnedServerRun`; the toolkit's local `HostingClientActor` places the fixture through `HostedWorldOnHost.ThisMachine`, journals it, saves, leaves, rehosts and cleans it up. The scenario asserts the marker before and after rehosting.

This advanced hosted test uses a source checkout; follow [contributor setup](../../CONTRIBUTING.md#set-up-and-validate-locally) before building it. The local-host profile stages plugins already present in the plan's `client.install`; a pin verifies a DLL but does not install it. Put the built `ExampleMod.dll` and `ExampleMod.TestAdapter.dll` under that install's `BepInEx/plugins` first. Use a disposable modded source install if your Steam install has unrelated plugins or you do not want to modify it. Launch this test from an interactive desktop terminal on Windows; an SSH/session-0 `dotnet test` cannot open a client.

Copy [`sample-hosted-plan.json`](ExampleMod.Tests/sample-hosted-plan.json) to the ignored `ExampleMod.Tests/hosted-dry-site-lifecycle.plan.json`, replace its pins and fixture paths, then run:

```sh
dotnet test examples/FullLifecycle/ExampleMod.Tests/ExampleMod.Tests.csproj -c Release \
  --filter "FullyQualifiedName~HostedMarkerSessionTests"
```

This explicit native test prepares the toolkit's clean `vtseedclean` character, refuses a same-named local or Cloud character, and retires its copy after a passing run has stopped its owned client. On a failed run it keeps the staged save and its registered source: verify the client is stopped before removing them. Use one client at a time with this seed. The run output is `session-runs/hosted-<id>/`, including the collected `host-world`; verify `valheim-test env status` is empty afterwards and the user's `worlds_local` matches its before-run snapshot. Without the private hosted plan, the test is skipped; the default no-game validation excludes it.

## Limits

The one-client mode proves persistence; the two-client mode proves simultaneous observation and B rejoin. Neither replaces the other. See [Platforms](../../docs/reference/platforms.md) for host requirements and the [native-run checklist](../../docs/guides/before-a-native-run.md) before launching.

An attached client remains its operator's process. The toolkit's [native acceptance suite](../../tests/Valheim.Testing.NativeAcceptance/README.md) uses a separate mod and is not an example to copy.
