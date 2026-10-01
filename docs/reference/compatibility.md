# Candidate compatibility policy for a first stable release

This is a review draft for [#135](https://github.com/tvongaza/ValheimTesting/issues/135), not a promise made by the current preview packages. The [generated API reference](index.md) inventories much of the current public surface; it does not decide which declarations should remain public. Review this policy and the inventory before removing the preview suffix.

| Package | Consumer-visible contract to review | Boundary and open decision |
| --- | --- | --- |
| `Valheim.Testing` | Pure terrain models, assertions and recorded-input formats | Decide which serialized fixtures are portable across versions and which are test inputs owned by the consumer. |
| `Valheim.Testing.Doubles` | Source-included stand-ins and their explicitly implemented behavior | The game-name stand-ins are not full Unity, Valheim, BepInEx or Jötunn implementations. Inventory those outside `Valheim.Testing.*` before freezing the package. |
| `Valheim.Testing.Cli` | Client transport types and wire replies built from the pinned ValheimCLI fork | The fork owns the transport and capability semantics. Record the supported fork revision and reject missing capabilities; do not silently assume an older CLI is equivalent. |
| `Valheim.Testing.Game` | Plans, result files, owned-process lifecycle, observations and failure results | Decide schema versions and whether older plans/results are read, migrated or explicitly refused. Preserve explicit failure when an observation is absent or incomplete. |
| `Valheim.Testing.Adapter` | Source-included helpers compiled into a mod's game-side adapter | Review extension registration, command replies, cleanup, and the public game types pulled into a consumer's compilation. |
| `Valheim.Testing.Bindings` | Library types and the meaning of missing/inaccessible findings | A game update can change findings without changing this package's API. |
| `Valheim.Testing.Bindings.Tool` | Command arguments, exit codes and machine-readable output | Freeze the documented command contract separately from library signatures. |

## Proposed versioning rule

After 1.0, follow semantic versioning for the *documented supported contract* of each package, with independent package versions. A compatible addition increments a minor version; a fix increments a patch version; removing or changing a supported member or accepted schema requires a major version and migration note. Preview APIs remain free to change before that baseline is chosen. Internal implementation and explicitly experimental APIs do not gain an accidental compatibility promise just because their types appear in generated documentation.

For source-included packages, compile compatibility matters in addition to binary compatibility: a change to a public partial double or adapter helper can break consumer source even when no DLL API check notices it. Keep clean consumer builds for both. For `Game` plans and evidence, version the serialized format explicitly and reject unknown required fields or unsupported versions rather than interpreting them as an older plan. Record the exact policy for older formats before 1.0; this paragraph does not assert that today's files already implement it.

Valheim game-build support is a separate matrix from NuGet package versioning. A newer Valheim release can invalidate native observations or a binding check without changing a library signature. Native runs must record the game build, plugin pins and CLI capabilities; an unsupported combination should fail clearly, not count as a passing assertion.

## Migration and baseline work still required

1. Classify generated public declarations as supported, experimental or accidental. In particular, inventory the game-name doubles excluded from the current reference and the fork-sourced CLI transport. Move accidental declarations behind an internal boundary where possible.
2. Write a preview-to-1.0 migration page with a compiling before/after consumer. Cover exact `runtimePins` and `installPins`, renamed plan fields and every chosen schema-version behavior.
3. Once the first stable packages exist, enable the .NET SDK's package validation against those published packages. It compares packaged APIs without maintaining a hand-written signature list. Add clean consumer builds for source packages and a deliberate schema-compatibility check for plans/results, which binary API validation cannot cover.
4. Confirm the chosen contract compiles for the framework examples and at least two mod consumers. Review exceptions to generated-documentation completeness rather than suppressing them globally.

The [package validation tooling](https://learn.microsoft.com/en-us/dotnet/fundamentals/apicompat/package-validation/overview) can enforce a baseline after a stable package is published. The current preview releases are useful for inspecting the candidate surface, but using them as a permanent compatibility baseline would freeze decisions #135 is meant to make.
