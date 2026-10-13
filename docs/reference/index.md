# API reference (preview)

This reference is generated from the current package sources. It is a candidate inventory of the public surface, not a promise that every listed type is supported in 1.0. The [compatibility policy](compatibility.md) lists the decisions still open.

For a released 0.1 package, use its [versioned API reference](https://tvongaza.github.io/ValheimTesting/versions/), built from the release tag. This page tracks current source and can be ahead of the packages on NuGet.org.

## Find a package's types

| Package | Types | Package page |
| --- | --- | --- |
| `Valheim.Testing` | [Pure terrain types](xref:Valheim.Testing) | [Valheim.Testing](../packages/Valheim.Testing.md) |
| `Valheim.Testing.Doubles` | [Toolkit doubles](xref:Valheim.Testing.Doubles); the game-name stand-ins have no reviewed inventory yet | [Valheim.Testing.Doubles](../packages/Valheim.Testing.Doubles.md) |
| `Valheim.Testing.Cli` | No generated reference yet; built from the pinned ValheimCLI fork | [Valheim.Testing.Cli](../packages/Valheim.Testing.Cli.md) |
| `Valheim.Testing.Game` | [Native runner types](xref:Valheim.Testing.Game); the namespace also holds the `Valheim.Testing.GameSessions` types | [Valheim.Testing.Game](../packages/Valheim.Testing.Game.md) |
| `Valheim.Testing.GameSessions` | [Game sessions](xref:Valheim.Testing.GameSessions.GameSession), [the pinned runner](xref:Valheim.Testing.GameSessions.PinnedServerRun) and hosts | [Valheim.Testing.GameSessions](../packages/Valheim.Testing.GameSessions.md) |
| `Valheim.Testing.NativeSmoke` | Command-line tool; no public library API | [Valheim.Testing.NativeSmoke](../packages/Valheim.Testing.NativeSmoke.md) |
| `Valheim.Testing.Adapter` | [Game-side helper types](xref:Valheim.Testing.Adapter) | [Valheim.Testing.Adapter](../packages/Valheim.Testing.Adapter.md) |
| `Valheim.Testing.Bindings` | [Binding-check types](xref:Valheim.Testing.Bindings) | [Valheim.Testing.Bindings](../packages/Valheim.Testing.Bindings.md) |
| `Valheim.Testing.Bindings.Tool` | Console entry point; delegates to `BindingCheckCommand` | [Valheim.Testing.Bindings.Tool](../packages/Valheim.Testing.Bindings.Tool.md) |

Two gaps are deliberate. The source-included game-name doubles sit outside the namespace filter, and the `Valheim.Testing.Cli` transport is built from another repository. Its source and a [transport guide at the pinned commit](https://github.com/tvongaza/valheimCLI/blob/e596a7d984feaaa695f5c2999964b64e23fbfd92/docs/client-library.md) are in the [ValheimCLI fork](https://github.com/tvongaza/valheimCLI); the commit is in [`cli-dependency.json`](../../cli-dependency.json).

## Pages with examples

| Page | Shows |
| --- | --- |
| [`CompositeTerrain`](xref:Valheim.Testing.CompositeTerrain) | Declaring synthetic terrain |
| [`WorldFixture`](xref:Valheim.Testing.Game.WorldFixture) | Copying a pinned world |
| [`DisposableCharacterStore`](xref:Valheim.Testing.Game.DisposableCharacterStore) | Registering a disposable character, from a game-created local save |
| [`GameActor`](xref:Valheim.Testing.Game.GameActor) | Verifying pins and observing a game process |
| [`PinnedServerRun`](xref:Valheim.Testing.GameSessions.PinnedServerRun) | Owning the server lifecycle |

An API page excerpts one call. For a complete scenario, follow the [guides](../index.md#find-the-guide-for-your-task).

## Related

- [Compatibility policy](compatibility.md): what is open before 1.0
- [Documentation scope](documentation-scope.md): which types must have rendered XML summaries
- [Packages](../package-versions.md): every package, version and pin
