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

var environment = RegressionEnvironment.Read(privateRegressionManifest);
environment.LoaderPackage = privateManifestPath;
var run = new TargetedRegression(environment);
run.Preflight(); // checks the package and copied install without launching Valheim
```

The package manifest and regression manifest are private: they contain machine paths. `Capture` records the files present in the extracted package, and `Read` refuses changed or missing files. On Windows, it also checks that `winhttp.dll` and `doorstop_config.ini` agree in the format they use. That static check is not a claim that Doorstop actually ran.

For `valheim-test start`, pass the captured manifest as `--loader-package`. For `valheim-test server-load` or `server-load-ab`, use `--loader-package` for the server and `--client-loader-package` when joining an unmodded client. Give each its own package built for that platform; no server package is silently copied to a client. The selected server package supplies the BepInEx references for the test adapter, and both identities enter the private result. See the [tool's README](../src/Valheim.Testing.NativeSmoke/README.md#disposable-native-mod-load-smoke).

When the native client opens successfully, the run records the package name, version and listing hash in provenance and marks the disposable copy as smoke-tested for those exact game and loader hashes. A changed package or game build invalidates the copy. A failed startup does not mark it tested. The normal run still opens the game to exercise the mod; a cached loader does not substitute for a native test result.
