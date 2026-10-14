# What the documentation check covers

CI requires an XML summary on every member of a small set of public types. This page lists those types and explains why the other generated declarations are outside that check.

The [generated API reference](https://tvongaza.github.io/ValheimTesting/reference/) lists every public declaration. A listed declaration is not a compatibility promise.

## Checked types

CI fails if a rendered member of one of these types has no XML summary.

| Layer | Namespace | Types |
| --- | --- | --- |
| Pure terrain | `Valheim.Testing` | `ITerrain`, `PlaneTerrain`, `CompositeTerrain`, `GridDumpTerrain` |
| Controlled assertions | `Valheim.Testing.Doubles` | `TerrainAssert` |
| Owned game work | `Valheim.Testing.Game` | `GameActor`, `ScenarioReport`, `SiteSearch`, `PlayerPlacement`, `WorldFixture`, `DisposableCharacterStore` |
| Sessions | `Valheim.Testing.GameSessions` | `GameSession`, `ClientActor`, `ServerActor`, `PinnedServerRun` |
| Game-side adapter | `Valheim.Testing.Adapter` | `TestExtension` |
| Static compatibility check | `Valheim.Testing.Bindings` | `BindingCheck` |

## Change the checked set

- `scripts/api-docs.cs` holds the exact type list. It checks every rendered member of these types, so an undocumented member fails CI.
- If you add a new supported operation type, add it to that list in the same PR.
- The public-surface check in `docs/reference/public-api/` inventories every public type and member. A change to it needs a reason. That review decides whether a new type joins the checked set.

## Outside the check

The other generated declarations have no summary requirement. Each has a reason:

- **Game-name doubles.** They are source-included and model a limited subset of Unity and Valheim.
- **Result and input shapes.** Each is described with the operation that owns it. Not all of them are user entry points.
- **`Valheim.Testing.Cli`.** It is built from the pinned ValheimCLI fork, which owns its transport and command contracts. See [ValheimCLI fork and release contract](../maintainers/valheimcli-release-contract.md).
- **`NativeSmoke` and `Bindings.Tool`.** They expose commands, not a library surface. Their package pages document the commands: [Valheim.Testing.NativeSmoke](../packages/Valheim.Testing.NativeSmoke.md) and [Valheim.Testing.Bindings.Tool](../packages/Valheim.Testing.Bindings.Tool.md).

These boundaries do not let a new supported operation go unexplained. The wider contract selection and the source-only compatibility audit are in the [Compatibility policy](compatibility.md).
