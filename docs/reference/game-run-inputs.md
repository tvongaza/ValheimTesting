# Loader packages and dependencies

How to pin a BepInEx loader package, and how the resolver finds a mod's pinned dependency set. A [targeted regression](sessions-targeted-regression.md) uses both.

## Pin a BepInEx loader

A Doorstop proxy and its configuration can pass a static shape check and still not start BepInEx in Valheim. The real check is an owned client reaching its menu with a fresh BepInEx log and the expected plugin identities; `ClientSession` makes it within `bepInExSeconds` and, on failure, keeps its Unity log (its launch's `-logFile`), the BepInEx log if written, and the boot output.

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
run.Preflight(); // checks the package and prepared profile without launching Valheim
run.Remove(); // retire the successful preflight's profile; a failed preflight attempts retirement itself
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

`NativeDependencyLock.ApplyTo(inputs, cliManifestPath)` fills a `RegressionInputs`' CLI, plugin and optional-reference fields. You supply the fixture, character and mod arms, then run `TargetedRegression.Preflight()` and the scenario, calling `Remove()` after a successful preflight, or `Remove(report, output)` after the last arm of a run ([cleanup](sessions-targeted-regression.md#clean-up-after-a-run)).

`start --no-mod` explicitly selects the same resolver's internal CLI-only route. Its lock has empty mods/plugins and exactly the real pinned CLI file; CLI hash, capability and declared-dependency checks remain required. Public `NativeDependencyResolver.Resolve` still refuses an empty mod request. This internal route accepts no search roots or optional-reference exceptions, so it never scans an install for an unselected plugin. The resulting `RegressionInputs.NoMod` is an explicit additive JSON opt-in, not an inference from an empty lock; see [control inputs](sessions-targeted-regression.md#optional-input-fields).

## Limits

- The static loader check is not a claim that Doorstop ran; the owned client's fresh BepInEx log is.
- Loader manifests, inventories, regression inputs and dependency locks are private: they contain machine paths. A public copy of a regression for review is made with [regression-bundle](../../tools/regression-bundle/README.md).
