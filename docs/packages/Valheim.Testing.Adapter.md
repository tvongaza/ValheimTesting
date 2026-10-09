# Valheim.Testing.Adapter

[Current preview API reference](https://tvongaza.github.io/ValheimTesting/). The package's README is this guide; use its versioned source commit from the package metadata when checking an older preview.

`Valheim.Testing.Adapter` is a source package compiled into a mod's **test-only** BepInEx adapter. It provides the mod's owned-session identity, a guarded global-key fixture change, and small helpers for mod-specific commands. Production mods do not reference it. The released package version is in the [package table](../getting-started.md#package-versions-and-feeds).

Generic observations belong to ValheimCLI's optional **Observe pack** (`Valheim.Cli.Observe.dll`, plugin `valheimCLI.observe`). Install and pin that pack on each server or client that needs its commands. It provides `valheim.observe/harmony`, `content-census`, `zones`, `custom-data`, `globalkeys`, `config`, `unresolved-prefabs`, `dungeon-rooms`, and the `review-*` commands. The toolkit checks each capability against the pinned manifest before launch and against `cli_extensions` after launch. A mod's adapter does not register these commands. See the ValheimCLI fork's command-pack guide for the pack boundary; the repository's [release contract](../valheimcli-release-contract.md) records the exact fork and bundle used by each release.

[FullLifecycle's adapter](../../examples/FullLifecycle/ExampleMod.TestAdapter/Plugin.cs) is the minimal pattern. The [native acceptance adapter](../../tests/Valheim.Testing.NativeAcceptance/AcceptanceMod.Adapter/Plugin.cs) adds commands for its own marker and a guarded fixture mutation. Build against the exact ValheimCLI core installed in the test runtime with [game references](../../tools/game-references/README.md).

## Registration and session identity

`TestExtension.Register(id, version, tokenVariable, modReady, registered, logError, commands...)` waits for ValheimCLI's extension API, registers the adapter's `session` capability, then its mod-specific commands. Run it as a coroutine from `Start` and dispose the registration in `OnDestroy`:

```csharp
private IEnumerator Start() => TestExtension.Register(
    "mymod.testing", "0.1.0", "MYMOD_TEST_SESSION_TOKEN",
    () => MyMod.Ready, registration => _registration = registration, Logger.LogError,
    MyModObservation.Command());
```

The session reports complete when the world is ready and `modReady` is true; `acceptingConnections` is separate. `OwnedServerSession.WaitUntilJoinable` waits for the owned server's socket before a client joins. If ValheimCLI's API is unavailable after 30 seconds, the adapter logs an error and remains inert. Dispose only this adapter's registration and Harmony ID; never call `Harmony.UnpatchAll()`.

## Remaining helpers

ValheimCLI owns the game-side extension API: registration and instance tokens, command roles, the devcommands and joined-client rules, the mutation gate, cancellation, cleanup and quiescence probes, result limits and `cli_extensions` discovery. Its reference is ValheimCLI's [extension API guide](https://github.com/tvongaza/valheimCLI/blob/9e8ca679298e559e995ab5b04ef84b782b15a0ad/docs/testing-toolkit.md#extension-api-v1), at the commit the released `Valheim.Testing.Cli` was built from; this repository does not keep a copy. What the toolkit adds:

| Helper | Purpose |
|---|---|
| `TestExtension` | Register the session identity and mod-specific extension commands once the CLI API is ready. |
| `FixtureGate` | Refuse a test-only mutation unless the owned process has the enabling variable and session token. |
| `GlobalKeyCommands.Change` | Register the shared, gated server-side `globalkey set|remove` fixture command; pair it with Observe's read-only `globalkeys` listing. |
| `Members` | Exact reflected lookup/call for private game members; missing or mistyped members throw. |
| `SavedObjects.InZone` | Bounded census of saved objects in one zone for a mod-specific observation. |
| `QuitLogFlush` | Preserve BepInEx lines emitted during a clean quit for the teardown log scan. |

`FixtureGate` and `Members` have unit tests; the source package also has a net48 compile check against declared game/Unity/CLI signatures. This does not prove that private reflected members exist in a new game build. A bounded native run is required for that boundary. The native acceptance suite exercises the session, saved-object and quit-log behavior.

### The quit-log flush

`QuitLogFlush.Enable()` belongs in the adapter's `Awake`; call `Quitting("<adapter> OnApplicationQuit")` from `OnApplicationQuit` and `Disable()` from `OnDestroy`. Do not arm it on a script reload: `OnDestroy` can run without the game quitting. The helper cannot preserve lines logged after managed code stops.

The ValheimCLI extension API owns command roles, devcommands and mutation gates, cancellation, quiescence, result-size limits and `cli_extensions` discovery. This package does not grant client admin rights or bypass those gates. A mod-specific command validates its own arguments. Game-side commands can be installed in `BepInEx/plugins` or, with ScriptEngine configured to load them at startup, `BepInEx/scripts`; do not install the same plugin in both places.
