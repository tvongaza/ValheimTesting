# Pin a BepInEx loader for a native run

A Doorstop proxy and its configuration can pass a static shape check yet fail to start BepInEx in Valheim. The definitive check is an owned client reaching its menu with a **fresh BepInEx log and the expected plugin identities**. `ClientSession` already makes that check within `bepInExSeconds`; if it fails, its private evidence keeps `Player.log`, the BepInEx log if written, and the boot output.

`BepInExLoaderPackage` lets a targeted regression or native mod-load smoke select one reviewed, extracted BepInEx/UnityDoorstop package instead of inheriting whichever loader files happen to be in the prepared game. It pins the package's core, proxy or library, and configuration by SHA256. The disposable install receives those files after the game is copied; the source game is never changed.

```sh
dotnet run scripts/bepinex-loader.cs -- capture /path/to/extracted-pack BepInExPack_Valheim 5.4.2202 /private/loader.json
dotnet run scripts/bepinex-loader.cs -- check /private/loader.json
```

From C# the same operation is:

```csharp
var loader = BepInExLoaderPackage.Capture(extractedPackageRoot, "BepInExPack_Valheim", "5.4.2202");
loader.Write(privateManifestPath);

// The loader is a property of the client environment: its loaderPackage in the inventory (here, this machine's client).
File.WriteAllText(privateEnvironmentsPath, $$"""{ "environments": [ { "name": "local-client", "roles": ["client"], "loaderPackage": {{JsonSerializer.Serialize(privateManifestPath)}} } ] }""");
var run = new TargetedRegression(RegressionInputs.Read(privateRegressionInputs), inventory: EnvironmentInventory.Read(privateEnvironmentsPath));
run.Preflight(); // checks the package and copied install without launching Valheim
```

The package manifest, the inventory file and the regression's inputs are private: they contain machine paths. `Capture` records the files present in the extracted package, and `Read` refuses changed or missing files. A package must be a complete loader for at least one platform by the files a launch needs: the preloader and `BepInEx.dll` in `BepInEx/core`, plus `winhttp.dll` and `doorstop_config.ini` on Windows, `doorstop_libs/libdoorstop_x64.so` on Linux, or `doorstop_libs/libdoorstop_x64.dylib` or a root `libdoorstop.dylib` on macOS; applying it, locally or on a host, refuses one that is not complete for that platform. On Windows, it also checks that `winhttp.dll` and `doorstop_config.ini` agree in the format they use. That static check is not a claim that Doorstop actually ran.

`valheim-test` runs every Windows and Linux profile on its pinned BepInExPack_Valheim when no package is named, never on the install's own loader; a session or campaign (`session.json`, `GameSessionFixture`, `PinnedServerRun.RunCampaignAsync`) does the same for every actor that names no `loaderPackage`, on this machine or another host, and records the pin as `<actor>LoaderShipped` in `result.json`'s provenance. A macOS install keeps its own native loader (#438). The rule and the explicit `BepInEx.cfg` are on the [tool's page](packages/Valheim.Testing.NativeSmoke.md#the-bepinex-loader).

For `valheim-test start`, pass the captured manifest as `--client-loader-package`. For `valheim-test server-load` or `server-load-ab`, use `--loader-package` for the server and `--client-loader-package` when joining an unmodded client. Give each its own package built for that platform; no server package is silently copied to a client. The selected server package supplies the BepInEx references for the test adapter, and both identities enter the private result. See the [tool's page](packages/Valheim.Testing.NativeSmoke.md#a-server-side-mod-server-load).

When the native client opens successfully, the run records the package name, version and listing hash in provenance. The normal run still opens the game to exercise the mod; a cached loader does not substitute for a native test result.
