# Choose an example

Start with [Your first gameplay assertion](../docs/tutorials/first-gameplay-assertion.md): place one marker in Valheim and independently assert that exactly one pole exists. Its [standalone file map](TargetedRegression/consumer/README.md) needs no toolkit clone.

| Example | What you test | Layer |
|---|---|---|
| [Marker one-shot](TargetedRegression/MarkerOneShot.cs) | The real command creates exactly one saved pole | Native gameplay |
| [ModWithTests](ModWithTests/README.md) | The production placement decision on supplied terrain | Unit; no game |
| [MarkerScenarioTests](FullLifecycle/ExampleMod.Tests/MarkerScenarioTests.cs) | Lost replies, unconfirmed saves and missing client visibility | Scripted integration; no game |
| [TargetedRegression](TargetedRegression/README.md) | The same scenario against parent and candidate builds | Native regression |
| [FullLifecycle](FullLifecycle/README.md) | Saved state survives restart and clients observe it | Native persistence and replication |
| [SharedWorld](SharedWorld/README.md) | Terrain composition, seams, snapshots and recorded-input parity | Unit; no game |
| [ObserveCheck](ObserveCheck/README.md) | A focused observation on an operator-owned disposable game | Attached native observation |

A [load check](../docs/tutorials/first-server-load-check.md) checks startup and world readiness. It does not assert gameplay. [Choose a test layer](../docs/guides/choose-a-test-layer.md) explains each layer's limits.

Coordinates in sample JSON are illustrative, not known Valheim sites. [Package versions](../docs/package-versions.md) lists the pins.

## Validation

`dotnet run scripts/validate.cs` builds the examples and runs their no-game checks. It does not start Valheim. Follow [CONTRIBUTING.md](../CONTRIBUTING.md) to validate toolkit changes.

## Run an example from the IDE

ObserveCheck and TargetedRegression are console programs. Set the documented arguments in your IDE's run configuration. The one-shot scenario is a class library loaded by `valheim-test`; follow its [tutorial](../docs/tutorials/first-gameplay-assertion.md).

## Not examples

| Tool | Purpose |
|---|---|
| [`valheim-test`](../docs/packages/Valheim.Testing.NativeSmoke.md) | Tool-owned native runs and environment checks |
| [`docker/linux-server/smoke`](../docker/linux-server/smoke/README.md) | The Linux image's boot check |
| [`tools/reload-check`](../tools/reload-check/README.md) | The ScriptEngine extension reload check |

## Contribute

See the [contribution guide](../CONTRIBUTING.md) for shared helpers and toolkit changes. Keep your mod's own assertions and fixtures in its repository.
