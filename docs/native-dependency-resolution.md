# Resolve native smoke dependencies

Before copying a game or launching it, `NativeDependencyResolver` inspects the selected mod DLLs, their hard BepInEx dependencies, direct assembly references and a hash-bound ValheimCLI build. It searches **only directories you name**. It never downloads a mod or decides that a missing assembly is safe to omit.

From a ValheimTesting checkout, create a private request such as:

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

Paths may be relative to the request file. Generate the ValheimCLI manifest for one known core-and-pack build with `CliCapabilityManifest.Generate(...)`, or use the manifest shipped with that build. The resolver selects the core and only packs providing the requested commands; it checks their hashes against that manifest. Include the commands your scenario needs as well as the runner's session commands.

```sh
sh scripts/bootstrap-cli.sh  # macOS/Linux; on Windows: pwsh -File scripts/bootstrap-cli.ps1
dotnet run scripts/native-dependencies.cs -- resolve /private/test/request.json /private/test/dependency-lock.json
dotnet run scripts/native-dependencies.cs -- check /private/test/dependency-lock.json
```

`resolve` writes a private, editable lock even when it finds a gap, then exits nonzero and names the missing or ambiguous DLL. Add a search root or choose an exact file and run it again. An assembly referenced by a soft integration is a **candidate**, not automatically optional: add its assembly name to `optionalReferences` only after confirming the mod guards that use when the integration is absent. The lock records each selected path, SHA256 and reason. `check` verifies those pins on later runs without rediscovery; it refuses a changed file.

An integration can call `NativeDependencyLock.ApplyTo(environment, cliManifestPath)` to fill `RegressionInputs`' CLI, plugin and optional-reference fields. The caller supplies the fixture, character and mod arms, then runs the existing `TargetedRegression.Preflight()` and native scenario. The lock remains private because it contains machine paths. This is the dependency step toward the one-command setup in [#156](https://github.com/tvongaza/ValheimTesting/issues/156); loader, fixture and process ownership are handled by their own setup steps.
