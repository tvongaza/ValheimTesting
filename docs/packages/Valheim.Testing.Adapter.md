# Valheim.Testing.Adapter

[API reference](https://tvongaza.github.io/ValheimTesting/reference/). This page is the package's README.

## Purpose

`Valheim.Testing.Adapter` is a source package that you compile into your mod's **test-only** BepInEx adapter. It gives the adapter an owned-session identity, a gated global-key fixture command and helpers for your own commands. Production mods never reference it.

Generic observations belong to the default `valheimCLI.dll`, not to a mod's adapter: `valheim.observe/harmony`, `content-census`, `zones`, `custom-data`, `globalkeys`, `config`, `unresolved-prefabs`, `dungeon-rooms` and the `review-*` commands. The [release contract](../maintainers/valheimcli-release-contract.md) records the exact ValheimCLI fork and bundle that each release uses.

For the minimal pattern, see [FullLifecycle's adapter](../../examples/FullLifecycle/ExampleMod.TestAdapter/Plugin.cs). The [native acceptance adapter](../../tests/Valheim.Testing.NativeAcceptance/AcceptanceMod.Adapter/Plugin.cs) adds commands for its own marker and a guarded fixture mutation.

## Install or pin

The released version is in the [package table](../package-versions.md).

Build the adapter against the exact ValheimCLI core installed in your test runtime. Use the [game references](../../tools/game-references/README.md) for this.

## Helpers

| Helper | Does | Fails when |
|---|---|---|
| `TestExtension` | Registers the `session` capability and your commands once ValheimCLI's API is ready. | The API is not ready after `ApiWaitSeconds` (30): it logs an error and stays inert. |
| `FixtureGate` | `Refusal` returns null only if the enabling variable is `1` and the token is set. | Otherwise it returns the refusal reason. An empty variable name throws `ArgumentException`. |
| `GlobalKeyCommands.Change` | Adds a server command, default name `globalkey`: `set <name> [value]` or `remove <name>`. | It fails `fixture_disabled` if the gate refuses, `usage` on bad arguments, and `no_world` without a world. |
| `Members` | Exact reflected `Field`, `StaticField`, `Method` and `Call` on private game members. | It throws `MissingFieldException`, `MissingMethodException` or `InvalidCastException`. It never returns a default. |
| `SavedObjects.InZone` | Lists one zone's saved objects (ZDOs), loaded or not, up to `limit`. | It throws if matches exceed `limit`, the zone is outside ±`MaxZoneIndex`, or no world is loaded. |
| `QuitLogFlush` | Flushes BepInEx's `LogOutput.log` while the game quits, for the teardown log scan. | It loses lines logged after managed code stops. |

Check the gate at the start of every fixture command:

```csharp
if (FixtureGate.Refusal("MYMOD_TEST_FIXTURES", "MYMOD_TEST_SESSION_TOKEN") is string refused)
{ context.Fail("fixture_disabled", refused); yield break; }
```

`GlobalKeyCommands.Change(enableVariable, tokenVariable)` runs this check for you. It replies `{source: "global-key-change", complete: true, action, name, value, keys}` with the server's keys after the change. Clients receive the new list with the server's next message. Read keys with the default plugin's read-only `globalkeys` listing.

`Members` fails loudly when a game update renames a member:

```csharp
var findObjects = Members.Method(typeof(ZDOMan), "FindObjects",
    typeof(Vector2s), typeof(List<ZDO>), typeof(HashSet<ZoneSystem.SectorIndex>));
```

## Register the adapter

`TestExtension.Register(id, version, tokenVariable, modReady, registered, logError, commands...)` waits for ValheimCLI's extension API. It then registers the `session` capability, followed by your commands. Run it as a coroutine from `Start`:

```csharp
private IEnumerator Start() => TestExtension.Register(
    "mymod.testing", "0.1.0", "MYMOD_TEST_SESSION_TOKEN",
    () => MyMod.Ready, registration => _registration = registration, Logger.LogError,
    MyModObservation.Command());
```

Dispose the registration in `OnDestroy`. Dispose only this adapter's registration and Harmony ID. Never call `Harmony.UnpatchAll()`.

The session reports `complete` when the world is ready and `modReady` returns true. `acceptingConnections` is a separate field. Before a client joins, call `OwnedServerSession.WaitUntilJoinable` to wait for the owned server's socket.

If ValheimCLI's API is not ready after 30 seconds, the adapter logs an error and stays inert.

## The quit-log flush

Put the three calls in your adapter plugin:

```csharp
private void Awake() => QuitLogFlush.Enable();
private void OnApplicationQuit() => QuitLogFlush.Quitting("<adapter> OnApplicationQuit");
private void OnDestroy() => QuitLogFlush.Disable();
```

Never call `Quitting` from `OnDestroy`. A script reload runs `OnDestroy` without quitting the game, so the flush would arm too early. The helper cannot save lines logged after managed code stops.

## Limits

- This package grants no client admin rights and bypasses no gate. ValheimCLI's [extension API guide](https://github.com/tvongaza/valheimCLI/blob/e596a7d984feaaa695f5c2999964b64e23fbfd92/docs/testing-toolkit.md#extension-api-v1) owns command roles, devcommands, the mutation gate, cancellation, quiescence, result limits and `cli_extensions` discovery.
- Your mod-specific command validates its own arguments.
- Tests without the game cannot prove that a private reflected member exists in a new game build. You need a bounded native run for that.
- Install game-side commands in `BepInEx/plugins` or in `BepInEx/scripts`, never both. `BepInEx/scripts` works only if ScriptEngine is configured to load them at startup.
