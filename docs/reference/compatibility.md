# Compatibility policy

This page proposes a compatibility policy for the first stable release. It is a draft for review. It is not a promise that the preview packages make.

The [generated API reference](index.md) lists much of the current public surface. It does not decide which declarations stay public. For what the documentation check inspects, see [What the documentation check covers](documentation-scope.md).

| Package | Contract to review | Open decision |
| --- | --- | --- |
| [`Valheim.Testing`](../packages/Valheim.Testing.md) | Pure terrain models, assertions and recorded-input formats. | Which serialized fixtures stay portable across versions, and which the consumer owns. |
| [`Valheim.Testing.Doubles`](../packages/Valheim.Testing.Doubles.md) | Source-included stand-ins and the behaviour they explicitly implement. | They are not full Unity, Valheim, BepInEx or Jötunn. Inventory game-name stand-ins first. |
| [`Valheim.Testing.Cli`](../packages/Valheim.Testing.Cli.md) | Client transport types and wire replies built from the pinned ValheimCLI fork. | The fork owns transport semantics. Record its revision and reject missing capabilities. |
| [`Valheim.Testing.Game`](../packages/Valheim.Testing.Game.md) | Plans, result files, owned-process lifecycle, observations and failure results. | Schema versions. Read, migrate or refuse older files. Absent observations still fail. |
| [`Valheim.Testing.Adapter`](../packages/Valheim.Testing.Adapter.md) | Source-included helpers compiled into a mod's game-side adapter. | Extension registration, command replies, cleanup and the game types pulled into consumers. |
| [`Valheim.Testing.Bindings`](../packages/Valheim.Testing.Bindings.md) | Library types and the meaning of missing or inaccessible findings. | A game update can change findings without changing the API. |
| [`Valheim.Testing.Bindings.Tool`](../packages/Valheim.Testing.Bindings.Tool.md) | Command arguments, exit codes and machine-readable output. | Freeze the command contract separately from library signatures. |

<!-- TODO(docs): the source policy has no rows for Valheim.Testing.GameSessions or Valheim.Testing.NativeSmoke; decide their contracts. -->

For an older CLI, do not assume it matches the pinned fork. The [ValheimCLI fork and release contract](../maintainers/valheimcli-release-contract.md) describes how the fork pin is tracked.

## Proposed versioning rule

After the first stable release, each package follows semantic versioning for its *documented supported contract*. Each package has its own independent version. The change decides the bump:

| Change | Version bump |
| --- | --- |
| Compatible addition | Minor |
| Fix | Patch |
| Removed or changed supported member or accepted schema | Major, with a migration note |

Preview APIs stay free to change until the baseline is chosen. Internal and explicitly experimental APIs make no compatibility promise, even when generated documentation lists their types.

### Source-included packages

Source-included packages need compile compatibility as well as binary compatibility. A change to a public partial double or adapter helper can break consumer source, and no DLL API check notices it. Keep clean consumer builds for both.

### Plans and evidence

Version the serialized format of `Game` plans and evidence explicitly. Reject unknown required fields and unsupported versions. Do not read them as an older plan. The policy for older formats is not recorded yet, and today's files do not necessarily implement it.

### Valheim game builds

Valheim game-build support is a separate matrix from NuGet package versions. A new Valheim release can invalidate native observations or a binding check without changing a library signature. Native runs must record the game build, plugin pins and CLI capabilities. An unsupported combination fails clearly. It never counts as a passing assertion.

## Work before the first stable release

1. Classify each generated public declaration as supported, experimental or accidental. Inventory the game-name doubles that the current reference excludes, and the fork-sourced CLI transport. Move accidental declarations behind an internal boundary where possible.
2. Write a migration page from preview to stable, with a compiling before-and-after consumer. Cover exact `runtimePins` and `installPins`, renamed plan fields and every chosen schema-version behaviour.
3. Once stable packages are published, enable the .NET SDK's package validation against them. It compares packaged APIs without a hand-written signature list. Add clean consumer builds for source packages and a schema-compatibility check for plans and results. Binary API validation covers neither.
4. Confirm the chosen contract compiles for the framework examples and at least two mod consumers. Review each exception to generated-documentation completeness. Do not suppress them globally.

The [package validation tooling](https://learn.microsoft.com/en-us/dotnet/fundamentals/apicompat/package-validation/overview) enforces a baseline once a stable package is published. Use the preview releases to inspect the candidate surface. Do not use them as a permanent baseline: that freezes decisions this policy is still making. Current package versions are on [Packages](../package-versions.md).
