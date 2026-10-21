# Standalone lifecycle consumers

Choose one source revision for every file below. Download or copy the linked files into an independent folder; no ValheimTesting checkout or repository imports are needed. [Package versions](../../../docs/package-versions.md) lists the published pins.

## Mod and adapter

Use the complete [MarkerMod file map](../../TargetedRegression/consumer/README.md) and its root `NuGet.Config`. Add these files beside `MarkerMod`:

| Source | Destination |
|---|---|
| [Adapter plugin](../ExampleMod.TestAdapter/Plugin.cs) | `MarkerAdapter/Plugin.cs` |
| [Package-only adapter project](MarkerAdapter/MarkerAdapter.csproj) | `MarkerAdapter/MarkerAdapter.csproj` |

The adapter supplies the owned session identity. It belongs only in the server's test profile. The tool supplies its game, loader and exact ValheimCLI build references through `--adapter-project`; you do not copy references from a toolkit checkout.

## Native persistence entry

Follow [Test that a change survives a save, restart and rejoin](../../../docs/guides/test-save-restart-rejoin.md) to prepare the campaign and generate the package-only server consumer. Replace its generated entry and add the two unchanged canonical scenario files:

| Source | Destination |
|---|---|
| [Entry](../MarkerPersistence.cs) | `MarkerPersistence/consumer/Program.cs` |
| [Plan](../ExampleMod.Tests/MarkerPlan.cs) | `MarkerPersistence/consumer/MarkerPlan.cs` |
| [Scenario](../ExampleMod.Tests/MarkerScenario.cs) | `MarkerPersistence/consumer/MarkerScenario.cs` |

Keep the generated `SmokeCheck.csproj` and `NuGet.Config`. The entry reads the emitted server plan with your feature expectations, uses the emitted client's password-variable binding, and delegates to `MarkerScenario`. `PinnedServerRun` prepares and retires the actors.

## Scripted lifecycle tests

For the same scenario's failure controls without a game, copy this complete map into `LifecycleTests`:

| Source | Destination |
|---|---|
| [Package-only project](LifecycleTests/LifecycleTests.csproj) | `LifecycleTests/LifecycleTests.csproj` |
| [Fixture](../ExampleMod.Tests/GameSessionFixture.cs) | `LifecycleTests/GameSessionFixture.cs` |
| [Session](../ExampleMod.Tests/ExampleSession.cs) | `LifecycleTests/ExampleSession.cs` |
| [Plan](../ExampleMod.Tests/MarkerPlan.cs) | `LifecycleTests/MarkerPlan.cs` |
| [Scenario](../ExampleMod.Tests/MarkerScenario.cs) | `LifecycleTests/MarkerScenario.cs` |
| [Scripted tests](../ExampleMod.Tests/MarkerScenarioTests.cs) | `LifecycleTests/MarkerScenarioTests.cs` |
| [Scripted world](../ExampleMod.Tests/TestWorld.cs) | `LifecycleTests/TestWorld.cs` |
| [Hosted scenario](../ExampleMod.Tests/HostedMarkerScenario.cs) | `LifecycleTests/HostedMarkerScenario.cs` |
| [Hosted tests](../ExampleMod.Tests/HostedMarkerSessionTests.cs) | `LifecycleTests/HostedMarkerSessionTests.cs` |
| [Dedicated sample](../ExampleMod.Tests/sample-plan.json) | `LifecycleTests/sample-plan.json` |
| [Hosted sample](../ExampleMod.Tests/sample-hosted-plan.json) | `LifecycleTests/sample-hosted-plan.json` |
| [NuGet configuration](../../TargetedRegression/consumer/NuGet.Config) | `NuGet.Config` |

From the independent folder:

```sh
dotnet test LifecycleTests/LifecycleTests.csproj -c Release --filter "Category!=Native"
```

Captured result:

```text
Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 180 ms - LifecycleTests.dll (net10.0)
```

The ten scripted controls include a lost mutation reply, an unconfirmed save, a missing Harmony patch and a marker invisible to the client. They do not prove native save or replication behavior. The native xUnit entry uses the exact `FullyQualifiedName~DrySiteSessionTests` filter; `Category=Native` also selects the separate hosted test.
