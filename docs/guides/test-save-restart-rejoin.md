# Test that a change survives a save, restart and rejoin

Check that what your mod changes in a world is still there after a confirmed save, a server restart and a client rejoin, with a real dedicated server and a real client.

## When to use this

Use it when your mod writes something into the world that players must keep: a placed object, a changed terrain, a stored value. The [FullLifecycle example](../../examples/FullLifecycle/README.md) is the runner to copy; it marks a site with a pole and checks the pole after a restart. To check only that a mod loads, use [check that a mod loads](check-a-mod-loads.md).

You need a Windows or macOS PC with Valheim and the Valheim Dedicated Server installed through Steam, Steam signed in, and a desktop session.

## Steps

### 1. Copy the example

Copy `examples/FullLifecycle/ExampleMod.Tests` beside your mod and rename it. Replace the mod's fields in `MarkerPlan.cs` and the steps in `MarkerScenario.cs`. Keep [GameSessionFixture.cs](../../examples/FullLifecycle/ExampleMod.Tests/GameSessionFixture.cs) and [ExampleSession.cs](../../examples/FullLifecycle/ExampleMod.Tests/ExampleSession.cs) as they are.

### 2. Declare your mod

Declare the Harmony patches your mod must apply. The run fails at runtime-ready, by name, if one is missing:

```csharp
public static readonly DeclaredPatch[] Patches = [new("Terminal::InitTerminal", "postfix", "ExampleMod.Plugin+RegisterCommands::Postfix")];
```

### 3. Write the rounds

The rounds are `first` and `after-restart`. Between them the run saves with a confirmed save, the client leaves, and only the server restarts:

```csharp
measure: round => round.Step("the client sees the marker at the dry site", () => RequireClientMarkers(round.Client, plan.DrySite, 1)),
afterRestart: round => round.Step("the server still has one marker at the dry site, none at the wet site",
    () => { RequireServerMarkers(round.Server, plan.DrySite, 1); RequireServerMarkers(round.Server, plan.WetSite, 0); }));
```

See the full call in [MarkerScenario.cs](../../examples/FullLifecycle/ExampleMod.Tests/MarkerScenario.cs). [Client rounds](../reference/game-client-rounds.md#client-rounds) lists what each round does. The session protects the joined player for you.

### 4. Lock each role's files

Write two dependency locks from a ValheimTesting checkout ([resolve dependencies](../reference/game-run-inputs.md#resolve-mod-dependencies)):

| Lock | Selects |
|---|---|
| Server | Your mod, its test adapter, their dependencies and the default ValheimCLI |
| Client | The default ValheimCLI only, so a client without your mod is what you test |

### 5. Write the session and the plan

Beside the test project, write two private files that `.gitignore` already lists:

- `session.json`: the dedicated server, one client named `client`, the fixture world, and each role's lock. With no `inventory`, every actor runs on this machine. [Campaigns](../reference/sessions-campaigns.md#the-session-manifest) lists every field.
- `dry-site-lifecycle.plan.json`: start from [sample-plan.json](../../examples/FullLifecycle/ExampleMod.Tests/sample-plan.json) and set your mod's fields. The run binds the installs, ports, runtime, world and pins from the prepared actors.

Your mod's fields are your expectations. Pick them from the fixture, never from the mod. These values are illustrative:

```json
"drySite": { "x": 120, "z": -380, "ground": 45.2 },
"wetSite": { "x": 900, "z": 150, "ground": 18.4 },
"arrival": { "x": 125, "z": -380, "ground": 45.0 },
```

Set the variable the client's `passwordVariable` names, `EXAMPLEMOD_TEST_PASSWORD`, in the shell you run from.

### 6. Check, then run

From the repository root:

```sh
valheim-test session check examples/FullLifecycle/ExampleMod.Tests/session.json --hosts
dotnet test examples/FullLifecycle/ExampleMod.Tests/ExampleMod.Tests.csproj -c Release --filter "Category=Native"
```

`session check` changes nothing, apart from one trial hard link for a Linux or Mac server's profile, made and removed under its runtime root. The test passes only when the run passed. Without the two private files, the native test is skipped with the reason.

<!-- TODO(docs): capture output of `valheim-test session check ... --hosts` -->

## Check the result

Each run writes a new directory under `session-runs/` in the test output:

| File | Holds |
|---|---|
| `result.json`, `junit.xml` | Every step with its phase and error ([phases and states](../reference/sessions-pinned-server-run.md#phases-and-states)) |
| `boot-N/` | One folder of logs per server boot |
| `connection-N.jsonl`, `client-commands.jsonl` | Every command sent |
| `client-process.json` | The owned client |

## Problems

| Symptom | Fix |
|---|---|
| The native test is skipped | Put `session.json` and `dry-site-lifecycle.plan.json` beside the test project |
| `session check` refuses the plan's plugins | Add each plugin the plan pins to that role's lock |
| The run fails at runtime-ready, naming a patch | The mod did not apply its declared patch: check the mod loads |
| The client is refused for its password | Set the `passwordVariable` variable in the runner's environment |
| `CleanupVerified` is false | Run `valheim-test env status` ([recovery](../reference/sessions-leases-and-recovery.md)) |

## Related

- [FullLifecycle example](../../examples/FullLifecycle/README.md)
- [Game sessions](../reference/sessions-game-session.md)
- [Pinned server runner](../reference/sessions-pinned-server-run.md)
- [Before a native run](before-a-native-run.md)
