# Choose an example

Bringing an existing mod? Start with [ModWithTests](ModWithTests/README.md) and [choose a test layer](../docs/guides/choose-a-test-layer.md). For package availability, see [package versions](../docs/package-versions.md).

| Example | Concept | Prerequisites |
|---|---|---|
| [ModWithTests](ModWithTests/README.md) | Unit-test the mod's real source with shared game doubles | .NET 10 SDK; no game or ValheimCLI |
| [SharedWorld](SharedWorld/README.md) | The pure terrain library: composed terrain, zone seams, snapshots, grid-dump parity | .NET 10 SDK; no game |
| [FullLifecycle](FullLifecycle/README.md) | One feature at every layer, from unit tests to an owned server and clients | Scripted tests: .NET 10 SDK. Native session: a game install, Steam and ValheimCLI |
| [TargetedRegression](TargetedRegression/README.md) | A/B regression: a parent and a candidate build of one mod in the real game | `preflight`: none. `run`: a prepared game install, copied to a disposable one |
| [ObserveCheck](ObserveCheck/README.md) | Attach to a prepared game, verify strict pins, run one probe, keep the report | A running disposable game with ValheimCLI (World Tools for terrain probes); never launches one |

Coordinates in sample JSON are illustrative, not known Valheim sites.

## Validation

`dotnet run scripts/validate.cs` builds every example. It runs SharedWorld and the ModWithTests tests, and it never starts Valheim.

## Run an example from the IDE

The native runners, ObserveCheck and TargetedRegression, are console programs that take command-line arguments. To run one from an IDE, set its arguments in the project's debug or run settings:

- Visual Studio: **Properties → Debug → Open debug launch profiles UI**
- Rider: **Run → Edit Configurations**

Native runs still need a disposable game install, world and character. See [before a native run](../docs/guides/before-a-native-run.md).

## Not examples

| Tool | Purpose |
|---|---|
| `valheim-test`, package `Valheim.Testing.NativeSmoke` ([package page](../docs/packages/Valheim.Testing.NativeSmoke.md)) | Disposable mod-load checks and A/B mod isolation |
| [`docker/linux-server/smoke`](../docker/linux-server/smoke/README.md) | The Linux image's boot check |
| [`tools/reload-check`](../tools/reload-check/README.md) | The ScriptEngine extension reload check |

## Contribute

To contribute an example or a missing shared helper, see the [contribution guide](../CONTRIBUTING.md).
