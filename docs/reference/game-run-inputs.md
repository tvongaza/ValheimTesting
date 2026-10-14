# Targeted regressions, loader packages and dependencies

The library contract behind a targeted A/B regression, a pinned BepInEx loader package and the resolver that finds a mod's pinned dependency set. The copyable template and its steps are in [TargetedRegression](../../examples/TargetedRegression/README.md).

## Targeted native regressions

`TargetedRegression` lives in [`Valheim.Testing.GameSessions`](../packages/Valheim.Testing.GameSessions.md); add that package to a runner that uses it ([versions](../package-versions.md)). It serves one shape: one owned client hosting one disposable fixture world, one mod under test in a parent and a candidate build, its dependencies and perhaps a probe. The run itself is the hosted [`ClientRounds`](game-client-rounds.md#client-rounds).

| Type or member | Does | Fails or refuses when |
|---|---|---|
| `RegressionInputs` | Holds what the run loads: the disposable character, the fixture root and UID, the default ValheimCLI plugin, other plugins, an optional probe and the mod's arms, each with its file, SHA256 and source commit | |
| `RegressionInputs.Read` | Reads the file and resolves relative paths against it | An unknown field |
| `new TargetedRegression(inputs, capabilities, inventory, clientEnvironment)` | Takes the machine from the inventory's client environment (its first, or the one named; this machine with no inventory) | The environment is not on this machine |
| `TargetedRegression.Read(regression.json)` | Uses the `environments.json` beside the file when there is one, as `valheim-test start` writes it for `--game`; otherwise this machine | |

From the client environment the regression takes: the game install, only read; the disposable loader profile `<runtime>/vt-prep-regression-<name>-<run-id>/runtime`; the port from `cliPort`; the loader from `loaderPackage`; the default slice from its resolved `architecture` (arm64 on a local Apple Silicon Mac, x64 elsewhere, unless the inventory specifies a slice). `client.architecture` in the regression JSON overrides the slice for this run. Each arm gets a fresh profile, staged with the same hosted-runtime path as other owned clients but holding only this regression's pinned allowlist; `result.json` records `disposableStageSeconds` for the arm.

`client.environment` sets non-secret process variables on the disposable game process, such as a mod's documented defer-generation switch. The same `environment` field exists on an owned `ClientRunPlan`; an attached plan refuses it. Validation rejects invalid names, multiline values and loader overrides before anything is copied or launched. It is not a credential channel: scenario evidence may include the plan.

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
| `Verify()` | Refuses any file added, changed or removed in the staged folders since staging |

| Member | Does |
|---|---|
| `Preflight()` | Stages every arm in turn |
| `Run(arm, output, scenario, rounds, measure)` | Stages the arm, writes `run-manifest.json`, calls `Verify()` right before the launch, runs the hosted `ClientRounds` with the scenario's capabilities required live before its first round, scans the client's logs and writes the report in every outcome |
| `Remove()` | Call it in `finally`: retires only the shared-stage copy this runner prepared and releases the host lock |

An interrupted run leaves its journal for `valheim-test env recover`.

## Pin a BepInEx loader

A Doorstop proxy and its configuration can pass a static shape check and still not start BepInEx in Valheim. The real check is an owned client reaching its menu with a fresh BepInEx log and the expected plugin identities; `ClientSession` makes it within `bepInExSeconds` and, on failure, keeps `Player.log`, the BepInEx log if written, and the boot output.

`BepInExLoaderPackage` lets a targeted regression or native mod-load smoke select one reviewed, extracted BepInEx/UnityDoorstop package instead of inheriting the loader files in the prepared game. It pins the package's core, proxy or library, and configuration by SHA256. The disposable install receives those files after the game is copied; the source game is never changed.

From a ValheimTesting checkout:

```sh
dotnet run scripts/bepinex-loader.cs -- capture /path/to/extracted-pack BepInExPack_Valheim 5.4.2202 /private/loader.json
dotnet run scripts/bepinex-loader.cs -- check /private/loader.json
```

`capture` writes the manifest at `/private/loader.json`; `check` refuses a changed or missing file. From C#:

```csharp
var loader = BepInExLoaderPackage.Capture(extractedPackageRoot, "BepInExPack_Valheim", "5.4.2202");
loader.Write(privateManifestPath);

// The loader is a property of the client environment: its loaderPackage in the inventory.
File.WriteAllText(privateEnvironmentsPath, $$"""{ "environments": [ { "name": "local-client", "roles": ["client"], "loaderPackage": {{JsonSerializer.Serialize(privateManifestPath)}} } ] }""");
var run = new TargetedRegression(RegressionInputs.Read(privateRegressionInputs), inventory: EnvironmentInventory.Read(privateEnvironmentsPath));
run.Preflight(); // checks the package and copied install without launching Valheim
```

`Capture` records the files present in the extracted package; `Read` refuses changed or missing files. A package must be a complete loader for at least one platform; applying it, locally or on a host, refuses one that is not complete for that platform.

| Platform | A complete package holds |
|---|---|
| All | The preloader and `BepInEx.dll` in `BepInEx/core` |
| Windows | `winhttp.dll` and `doorstop_config.ini`, which must agree in the format they use |
| Linux | `doorstop_libs/libdoorstop_x64.so` |
| macOS | `doorstop_libs/libdoorstop_x64.dylib` or a root `libdoorstop.dylib` |

When no package is named, `valheim-test` runs every Windows and Linux profile on its pinned BepInExPack_Valheim, never on the install's own loader. A session or campaign (`session.json`, `GameSessionFixture`, `PinnedServerRun.RunCampaignAsync`) does the same for every actor without a `loaderPackage`, on this machine or another host, and records the pin as `<actor>LoaderShipped` in `result.json`'s provenance. A macOS install keeps its own native loader. The rule and the explicit `BepInEx.cfg` are on the [NativeSmoke page](../packages/Valheim.Testing.NativeSmoke.md).

For `valheim-test start`, pass a captured manifest as `--client-loader-package`. For `server-load` and `server-load-ab`, use `--loader-package` for the server and `--client-loader-package` when joining an unmodded client. Give each its own package built for that platform; no server package is copied to a client. The selected server package supplies the BepInEx references for the test adapter, and both identities enter the private result. The tool's options are on the [NativeSmoke page](../packages/Valheim.Testing.NativeSmoke.md). When the native client opens, the run records the package name, version and listing hash in provenance.

## Resolve mod dependencies

Before copying or launching a game, `NativeDependencyResolver` inspects the selected mod DLLs, their hard BepInEx dependencies, direct assembly references and a hash-bound ValheimCLI build. It searches only directories you name. It never downloads a mod or decides that a missing assembly is safe to omit.

A private request; paths may be relative to the request file:

```json
{
  "mods": ["/private/test/mod/MyMod.dll"],
  "searchRoots": ["/private/test/dependencies"],
  "gameManaged": "/private/test/Valheim/valheim_Data/Managed",
  "bepInExCore": "/private/test/Valheim/BepInEx/core",
  "cliManifest": "/private/test/valheimcli/build-manifest.json",
  "cliFiles": "/private/test/valheimcli",
  "capabilities": ["valheim.session/state", "valheim.session/save", "valheim.session/leave"],
  "optionalReferences": []
}
```

`cliManifest` is the manifest of one pinned default-plugin build: generate it with `CliCapabilityManifest.Generate(...)` or use the one shipped with that build. The resolver selects the default plugin and checks its hash and the requested commands against it. List the commands your scenario needs beside the runner's session commands.

From a ValheimTesting checkout ([set up](../../CONTRIBUTING.md#set-up-and-validate-locally)):

```sh
dotnet run scripts/bootstrap-cli.cs
dotnet run scripts/native-dependencies.cs -- resolve /private/test/request.json /private/test/dependency-lock.json
dotnet run scripts/native-dependencies.cs -- check /private/test/dependency-lock.json
```

| Command | Does | Fails when |
|---|---|---|
| `resolve` | Writes a private, editable lock recording each selected path, SHA256 and reason, even when it finds a gap | A missing or ambiguous DLL: exits nonzero and names it. Add a search root or choose an exact file and run again |
| `check` | Verifies the lock's pins without rediscovery | A changed file |

An assembly a soft integration references is a candidate, not automatically optional. Add its assembly name to `optionalReferences` only after confirming the mod guards that use when the integration is absent.

`NativeDependencyLock.ApplyTo(inputs, cliManifestPath)` fills a `RegressionInputs`' CLI, plugin and optional-reference fields. You supply the fixture, character and mod arms, then run `TargetedRegression.Preflight()` and the scenario.

## Limits

- One owned client hosting one world.
- The checks read what BepInEx reads before loading; a dependency found by reflection at run time is caught only live.
- The static loader check is not a claim that Doorstop ran; the owned client's fresh BepInEx log is.
- The regression holds the inventory host lock until its copy is retired.
- Loader manifests, inventories, regression inputs and dependency locks are private: they contain machine paths. A public copy of a regression for review is made with [regression-bundle](../../tools/regression-bundle/README.md).
