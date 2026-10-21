# Targeted regression

`TargetedRegression` runs one scenario on two builds of a mod, a parent and a candidate, with everything else pinned. You stage each arm without the game, then run each arm and compare the two results.

It is in [`Valheim.Testing.GameSessions`](../packages/Valheim.Testing.GameSessions.md) ([versions](../package-versions.md)). [Run an A/B regression](../guides/run-an-ab-regression.md) walks through it, and the [TargetedRegression example](../../examples/TargetedRegression/README.md) is the runner to copy.

## Inputs and the machine

It serves one shape: one owned client hosting one disposable fixture world, one mod under test in a parent and a candidate build, its dependencies and perhaps a probe. The run itself is the hosted [`ClientRounds`](game-client-rounds.md#client-rounds).

| Type or member | Does | Fails or refuses when |
|---|---|---|
| `RegressionInputs` | Holds what the run loads: the disposable character, the fixture root and UID, the default ValheimCLI plugin, other plugins, an optional probe and the mod's arms, each with its file, SHA256 and source commit | |
| `RegressionInputs.Read` | Reads the file and resolves relative paths against it | An unknown field |
| `new TargetedRegression(inputs, capabilities, inventory, clientEnvironment)` | Takes the machine from the inventory's client environment (its first, or the one named; this machine with no inventory) | The environment is not on this machine |
| `TargetedRegression.Read(inputsFile)` | Uses the `environments.json` beside the file when there is one, as `valheim-test start` writes it for `--game`; otherwise this machine | |

From the client environment the regression takes: the game install, only read; the disposable loader profile `<runtime>/vt-prep-regression-<name>-<run-id>/runtime`; the port from `cliPort`; the loader from `loaderPackage`; the default slice from its resolved `architecture` (arm64 on a local Apple Silicon Mac, x64 elsewhere, unless the inventory specifies a slice). `client.architecture` in the regression JSON overrides the slice for this run. Each arm gets a fresh profile, staged with the same hosted-runtime path as other owned clients but holding only this regression's pinned allowlist; `result.json` records `disposableStageSeconds` for the arm.

`client.environment` sets non-secret process variables on the disposable game process, such as a mod's documented defer-generation switch. The same `environment` field exists on an owned `ClientRunPlan`; an attached plan refuses it. Validation rejects invalid names, multiline values and loader overrides before anything is copied or launched. It is not a credential channel: scenario evidence may include the plan.

## Fixture and character

The fixture contains exactly one saved world. `WorldIdentity.Read(root).UidText` reads its UID from metadata; the filename is not its identity.

| Input | Does | Constraint |
|---|---|---|
| `DefaultSmokeWorld.Prepare` | Copies the packaged smoke world into a new directory | Use a fresh source directory |
| `DefaultSmokeCharacter.Ensure` | Supplies the packaged disposable character store | Copies share one player ID |
| `client.character` | Names the local character staged for the run | With `characterStore`, a same-named local or Cloud save is refused |
| `client.characterStore` | Names a `DisposableCharacterStore` with that registered character | Use a game-created disposable character |

With `characterStore`, `run` stages the registered character before launch and removes its file and backups after the owned client stops, including on failure. Without that field, stage your disposable local character yourself.

## Optional input fields

| Field | Does | Fails or refuses when |
|---|---|---|
| `mod.repeatability` | Allows arms with the same SHA256 | Otherwise identical arms are refused |
| `configs` | Stages named BepInEx config files | Files do not match their declared inputs |
| `patchers` | Stages the declared preloader patchers | A file is missing or changed |
| `optionalReferences` | Names optional assembly references | An unresolved reference is not listed |
| `gamePins` | Pins the game, selected loader and patchers | A pin differs |
| `client.startSeconds`, `client.joinSeconds` | Bound menu startup and each hosted start | Outside 10–1800 or 10–900 seconds respectively |
| `client.environment` | Supplies non-secret variables to the owned process | Invalid names, multiline values or loader overrides |
| `logScan` | Declares expected error headers and their reasons | New or different errors appear |

See the canonical [input sample](../../examples/TargetedRegression/regression.sample.json) for the JSON shape and [log scan](game-log-scan.md) for expected-error declarations. Without a supplied `valheimCLI.valheimCLI.cfg`, staging writes the environment's port.

## Stage checks

`TargetedRegression.Stage(arm)` works without the game and refuses at the first problem, naming the field and the fix.

| Check | Refuses |
|---|---|
| Fixture layout (`FixtureLayout.Discover`) | A root that is the world folder itself, a `worlds_local` wrapper, several worlds or none; a world whose metadata holds another UID (`HostWorldPlan.Preflight`) |
| Hashes | A file missing or not its pinned SHA256 (an empty `sha256` prints the hash to review); an arm whose file is another build than its commit's; two arms with one SHA256 unless `mod.repeatability` |
| Clean install | An existing profile not prepared by this run. The stage prepares a fresh loader profile (the game is never copied) and rebuilds `BepInEx/plugins`, `patchers`, `config` and `scripts` from the allowlist; only the selected arm is installed |
| Declared metadata (`PluginMetadata.Read`, no assembly loaded) | A hard `[BepInDependency]` no staged `[BepInPlugin]` declares, or one older than its minimum version; a staged `[BepInIncompatibility]`; a `[BepInProcess]` that excludes the client; a GUID declared twice; an assembly reference nothing provides, unless listed in `optionalReferences` |
| Plan | The disposable character missing from `characters_local`; then the strict plan's `Validate` and `Preflight` on the staged install, which with `cli.manifest` includes the [static ValheimCLI capability check](game-preflight.md#valheimcli-capability-manifest) |

File names never count in the metadata check. A probe's or the mod's own commands are checked live only, before the first round.

`Stage` returns a `StagedArm`:

| Member | Does |
|---|---|
| `Plan` | The strict `ClientRunPlan`: every staged plugin's declared GUID pinned by its MD5, no `absent` pins for anything else |
| `Manifest` | The `RunManifest`: the arms, the allowlist with SHA256 and MD5, the install pins, the world and capabilities, install-relative paths only |
| `Verify()` | Refuses any file added, changed or deleted in the staged folders since staging |

| Member | Does |
|---|---|
| `Preflight()` | Stages every arm in turn; the last stays staged ([clean up](#clean-up-after-a-run)) |
| `Run(arm, output, scenario, rounds, measure)` | Stages the arm, writes `run-manifest.json`, calls `Verify()` right before the launch, runs the hosted `ClientRounds` with the scenario's capabilities required live before its first round, scans the client's logs and writes the report in every outcome |
| `Remove()` | Retires only this runner's prepared profile and releases its host lock ([cleanup](#clean-up-after-a-run)) |

## Clean up after a run

A successful `Stage` or `Preflight` keeps its profile under the client's journalled host lock. A returned `Run` report keeps it for another arm. Call `Remove()` when finished, or `Remove(report, output)` after the last arm to record retirement in its report.

Retire a successful preflight before printing its snapshots:

```csharp
var regression = TargetedRegression.Read(inputsFile);
var arms = regression.Preflight();
regression.Remove();
foreach (var arm in arms)
    foreach (string line in arm.Describe()) Console.WriteLine(line);
```

The output lists each arm's identity and pinned files. Nothing launches.

If `Stage`, `Preflight` or `Run` throws, it attempts retirement before propagating the error. This includes a failed report write. It preserves operation and cleanup failures together, without automatically retrying failed retirement. `Remove(report, output)` also preserves both errors if retirement and writing fail.

A normal scenario failure returns a failed report. Call `Remove(report, output)` before checking `report.Passed`, as the [example](../../examples/TargetedRegression/Program.cs) does. A failed retirement keeps unretired state and its host lock for recovery; it never deletes an unowned path.

An interrupted run leaves its journal for `valheim-test env recover` ([recovery](sessions-leases-and-recovery.md#env-recover)).

## Limits

- It serves one owned client hosting one world. For a dedicated server or several clients, use a [session](sessions-game-session.md).
- It holds the client host's lock until its profile is retired.
- The strict plan it builds hosts the world (`hostWorld`), so a scenario cannot use [`LogoutCycle`](game-client-rounds.md#log-out-and-log-in), which refuses a hosting client.
- The metadata checks read what BepInEx reads. Only the live run checks a dependency a mod finds by reflection.
