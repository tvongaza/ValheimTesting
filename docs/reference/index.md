# ValheimTesting API reference (preview)

This reference is generated from the current package sources. It is a **candidate public-surface inventory**, not a promise that every listed type is supported in 1.0. Issue [#135](https://github.com/tvongaza/ValheimTesting/issues/135) selects that contract; [#140](https://github.com/tvongaza/ValheimTesting/issues/140) supplies the behavior and failure documentation it needs.

Start with [Bring your mod](https://github.com/tvongaza/ValheimTesting/blob/main/docs/adopting.md) to choose a package and test layer. The [getting-started guide](https://github.com/tvongaza/ValheimTesting/blob/main/docs/getting-started.md) has current package versions; each package's [reference page](https://github.com/tvongaza/ValheimTesting/tree/main/docs/packages) explains its contract and limits. The [ValheimCLI fork](https://github.com/tvongaza/valheimCLI) owns game-side command and capability semantics. The external `Valheim.Testing.Cli` transport is built from the exact fork commit in [`cli-dependency.json`](https://github.com/tvongaza/ValheimTesting/blob/main/cli-dependency.json); its generated type reference is still to be integrated under #140.

For short examples on selected API pages, see [declaring synthetic terrain](xref:Valheim.Testing.CompositeTerrain), [copying a pinned world](xref:Valheim.Testing.Game.WorldFixture), and [registering](xref:Valheim.Testing.Game.DisposableCharacterStore) a disposable character. The character examples start from a game-created local save; they do not synthesize a player. For complete runnable flows, follow the linked guides and examples.

For game-facing tests, [pin and observe through `GameActor`](xref:Valheim.Testing.Game.GameActor), then let [`PinnedServerRun` own the server lifecycle](xref:Valheim.Testing.Game.PinnedServerRun). Their excerpts link to compiling examples; the API page is not a replacement for the complete scenario.

The source-only `Valheim.Testing.Adapter` reference is extracted from the same source compiled by `tests/Valheim.Testing.Adapter.CompileCheck`, with unrelated game/reference stubs filtered out. `Valheim.Testing.Doubles` is also shipped as source. Its declarations intentionally model only selected game behavior; consult [game doubles](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.Doubles.md) before using them as evidence of native behavior.

| Package | Reference in this preview | Package page |
| --- | --- | --- |
| `Valheim.Testing` | [Pure terrain types](xref:Valheim.Testing) | [Valheim.Testing](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.md) |
| `Valheim.Testing.Doubles` | [Toolkit doubles](xref:Valheim.Testing.Doubles); the game-name stand-ins still need a reviewed inventory | [Valheim.Testing.Doubles](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.Doubles.md) |
| `Valheim.Testing.Cli` | Generated type reference pending; built from the pinned ValheimCLI fork | [Valheim.Testing.Cli](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.Cli.md) |
| `Valheim.Testing.Game` | [Native runner types](xref:Valheim.Testing.Game) (the namespace also holds `Valheim.Testing.GameSessions`' types) | [Valheim.Testing.Game](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.Game.md) |
| `Valheim.Testing.GameSessions` | [Game sessions](xref:Valheim.Testing.Game.GameSession), [the pinned runner](xref:Valheim.Testing.Game.PinnedServerRun) and hosts (namespace `Valheim.Testing.Game`) | [Valheim.Testing.GameSessions](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.GameSessions.md) |
| `Valheim.Testing.Adapter` | [Game-side helper types](xref:Valheim.Testing.Adapter) | [Valheim.Testing.Adapter](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.Adapter.md) |
| `Valheim.Testing.Bindings` | [Binding-check types](xref:Valheim.Testing.Bindings) | [Valheim.Testing.Bindings](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.Bindings.md) |
| `Valheim.Testing.Bindings.Tool` | Console entry point delegates to `BindingCheckCommand` | [Valheim.Testing.Bindings](https://github.com/tvongaza/ValheimTesting/blob/main/docs/packages/Valheim.Testing.Bindings.md#in-ci) |

This table deliberately names two gaps: source-included game-name doubles are outside the namespace filter, and the transport is built from another repository. Neither can be treated as a frozen contract solely because this site builds. See the [candidate compatibility policy](compatibility.md) for the decisions still needed before 1.0.
