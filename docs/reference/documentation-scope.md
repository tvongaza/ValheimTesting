# What the 0.1 documentation check covers

The generated pages inventory public declarations; they do not make every declaration a compatibility promise. For the first public beta, CI requires a rendered XML summary on each member of these selected operation entry points:

| Layer | Checked entry points |
| --- | --- |
| Pure terrain and controlled assertions | `ITerrain`, `PlaneTerrain`, `CompositeTerrain`, `GridDumpTerrain`, `TerrainAssert` |
| Owned game work | `GameActor`, `ScenarioReport`, `SiteSearch`, `PlayerPlacement`, `WorldFixture`, `DisposableCharacterStore` |
| Sessions | `GameSession`, `ClientActor`, `ServerActor`, `PinnedServerRun` |
| Game-side adapter | `TestExtension` |
| Static compatibility check | `BindingCheck` |

`scripts/api-docs.cs` holds the exact type list. It checks every rendered type member, so adding an undocumented member to one of these types fails CI. A new supported operation type should be added to that list in the same PR. The separate public-surface check inventories *all* public types and members and requires a reason for a change; it is the review point for deciding whether a new type joins this documented set.

The remaining generated declarations are outside this **entrypoint-summary** gate for explicit reasons: source-included game-name doubles model a limited subset of Unity and Valheim; result and input shapes are described with their owning operation and are not all user entry points; `Valheim.Testing.Cli` is built from the pinned ValheimCLI fork, which owns its transport and command contracts; `NativeSmoke` and `Bindings.Tool` expose commands documented on their package pages, rather than a library surface. These are documentation boundaries, not permission to leave a newly supported operation unexplained. The wider contract selection and source-only compatibility audit remain in [the 1.0 policy](compatibility.md).
