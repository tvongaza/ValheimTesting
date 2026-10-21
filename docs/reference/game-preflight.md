# Owned-run preflight

The static checks an owned run makes before anything is staged or started, and the ValheimCLI capability manifest they compare against.

`ClientRunPlan.Preflight()` runs after `Validate` and before anything is copied or started. Hosted [client rounds](game-client-rounds.md#client-rounds) run it as their first step, with `CliCapabilities.HostedRounds` passed through `Preflight(capabilities)`. `ClientSession.Launch` runs the install part itself, before it reserves the CLI port or looks for Steam. A mod runner can call it in its own `validate` mode. An attached client's install is its operator's and is never read, so only a hosted fixture is checked for it. The [strict per-command pins](game-actors-and-pins.md#strict-pins-from-tests) are unchanged: the preflight is an earlier diagnostic, not a relaxation.

## What the preflight catches

| Problem | Caught | What is checked |
|---|---|---|
| A Doorstop 4 `winhttp.dll` beside a Doorstop 3 `doorstop_config.ini`: the game reaches its menu without BepInEx | Static, loader check (servers too) | The proxy's version from the key its code reads; the file must match it (see below) |
| The game runs, but BepInEx never starts | Live, right after launch | This launch's first line in `BepInEx/LogOutput.log` within `bepInExSeconds` (default 60) |
| ValheimCLI in `BepInEx/scripts` with ScriptEngine's `LoadOnStart = false` (its default) | Static | A pinned plugin in `scripts` needs ScriptEngine pinned in `plugins` and `[General] LoadOnStart = true` in `BepInEx/config/com.bepis.bepinex.scriptengine.cfg` |
| An old monolithic ValheimCLI with no `cli_extensions`; a default plugin without the required built-ins | Live, once the client answers | `CliCapabilities.Require(actor, paths)` reads `cli_extensions` once and names every missing command |
| The wrong ValheimCLI generation, found before anything starts | Static, with a `cliManifest` | The staged files match the manifest by SHA256 and offer every capability the run uses |
| A standing expectations file with several pins on one line | Static, when ValheimCLI's config sets `[Expectations] File` | `StandingPins.Parse` and `Read` name the line; `Format` and `Write` write one pin per line |
| A world fixture the game would not load as it is: incomplete, holding extra saves or chunks, or saved before Valheim 1.0 | Static, before anything is staged or copied | `WorldIdentity.Read(fixture)`, the one check of every world fixture (see below) |
| A reused world name hiding a snapshot with another UID | Static, before the fixture is copied | `WorldIdentity.Read(fixture)` reads the fixture's own metadata; `HostWorldPlan.Preflight()` and `ServerRunPlan.PreflightWorld()` refuse a world UID it does not hold |
| A hand-typed candidate file name or hash | Static | `InstallPins.Plugins(install, files)` refuses a name not in the folder; `WorldFixture.Verify` refuses changed fixture files |

Details per row:

- **Doorstop pair.** Doorstop 4 reads `target_assembly`, Doorstop 3 reads `targetAssembly`. A Doorstop 4 proxy needs a file with `[General]` and no `[UnityDoorstop]`; a Doorstop 3 proxy needs `[UnityDoorstop]`. A proxy that shows neither key, or both, is refused: its configuration cannot be matched to it.
- **BepInEx never starts.** Lines from an earlier run never count. For a client in a host's desktop session, a preloader crash log (`preloader_*.log` beside the game) written by this launch is quoted in the failure (its first `[Error` or `[Fatal` line) and kept as `game-2.preloader-N.log`. An older one is only named in `preloader.stale`.
- **ScriptEngine.** The refusal also offers the simpler layout: one default `valheimCLI.dll` in `plugins`.
- **Live capabilities.** `CliCapabilities.Require` names the default plugin that provides each missing command, or says the ValheimCLI predates the built-in command API. `GameActor.RequireCapability` gives the same messages.
- **Standing expectations.** The preflight also refuses a configured file that is missing, a strict one (`Strict = true`) that names no world (`world=any` lets the transition through while the actor still pins the exact UID), and one that contradicts the plan's pins or hosted world UID.
- **World fixture.** A fixture holds one Valheim 1.0 world folder `<name>/`, exactly as the game saved it, and nothing else. `WorldIdentity.Read` refuses, naming the file and what to do:
  - a save that is not all four `_main.<n>.fwl2`, `.db2`, `.chunks` and `.ok` files. The one exception is a world never saved: a lone `_main.0.fwl2` whose metadata says so. Without its data the game silently makes a fresh world.
  - a second save set, or a `.chunk` file that `_main.<n>.chunks` does not list. The game deletes both when it loads the world, so the run would not load the pinned files. A listed chunk that is missing fails the game's load.
  - a pre-1.0 world (`<name>.fwl` and `<name>.db`). Open it once in Valheim 1.0 or later and save it; the game then writes the `<name>/` folder.
  - anything else at the root, or in the world folder.

  The game's backups beside the world folder (`<name>_backup_auto-<yyyyMMdd-HHmmss>/`, or `_backup_` and `_backup_restore-` with the same date) may stay; they are checked for the world's name and UID only. A dedicated server's fixture is its `-savedir`: one such world in `worlds_local/`. Every route reads it before staging: hosted clients (`HostWorldPlan.Preflight`), `PinnedServerRun` and campaigns (`ServerRunPlan.PreflightWorld`), and `valheim-test server-load --world-fixture`, `server-load-phases` and `detach server-load`.
- **World identity.** The metadata (`<name>/_main.<n>.fwl2`) is a length-prefixed package: world version, name, seed name, seed, UID. After the start, another UID fails as the wrong world, naming both UIDs, never as a load failure.
- **Plugin pins.** A pinned MD5 that no DLL in `BepInEx/plugins` or `BepInEx/scripts` has is refused, naming similar files and their hashes; so is one installed more than once. `FileHash.Md5(path)` is the hash `cli_manifest` reports.

Every check of an install on another host uses `BepInExLoader.LoaderFiles`, the one list of loader files each platform needs: the Windows proxy and configuration or the Linux `doorstop_libs/libdoorstop_x64.so`, beside the preloader and core; on macOS either `libdoorstop.dylib` or `doorstop_libs/libdoorstop_x64.dylib`. A remote client's files are checked for existence in one round trip; only the Windows proxy and configuration are read.

## ValheimCLI capability manifest

A staged ValheimCLI can be the wrong generation: an old monolithic build, or a plugin whose file name and GUID look right but which lacks `valheim.session/state`, `save` or `leave`. Only the DLL's hash tells these apart. A `CliCapabilityManifest` records one ValheimCLI build, which is one plugin: its file name (`CliCapabilityManifest.PluginFile`, `valheimCLI.dll`), its SHA256, the BepInEx GUID it declares (`CliCapabilityManifest.PluginGuid`, `valheimCLI.valheimCLI`) and the extension commands it registers with their result versions.

Illustrative values; the hash and commit are placeholders:

```json
{
  "schema": 1,
  "build": "tvongaza/valheimCLI <commit>",
  "files": [
    { "file": "valheimCLI.dll", "sha256": "<64 hex>", "plugins": ["valheimCLI.valheimCLI"],
      "extensions": { "valheim.session": { "join": 1, "leave": 1, "save": 1, "state": 1 },
                      "valheim.world": { "terrain": 1 }, "valheim.observe": { "zones": 1 } } }
  ]
}
```

Which manifest an owned client is checked against:

| Client | Checked against |
|---|---|
| Disposable copy (the default) | The staged set's manifest: the plan's `cliManifest`, else the pinned bundle's |
| Run in place (`inPlace`) | Its `cliManifest`, when it names one; otherwise no static check |
| Attached | None: `Validate` refuses a `cliManifest`; capabilities are required live |

Before the copy exists, the runner's own capabilities (the hosted rounds' session commands, the arrival waits) are checked against a named manifest and recorded, so the copy's check before launch covers them. Only ValheimCLI-owned capabilities (`valheim.*`, `cli.*`) are checked statically; a mod's own extensions are confirmed live after its plugin loads.

On the install it checks, `ClientRunPlan.Preflight()` refuses before anything is started:

- a manifest file that is missing (`FileNotFoundException`) or malformed (`InvalidDataException`); it is never treated as absent;
- the manifest's plugin when no file in `BepInEx/plugins` or `BepInEx/scripts` (any subfolder) has its SHA256, or when it is installed more than once;
- any other DLL there with the plugin's file name, or declaring its GUID (read from `[BepInPlugin]` metadata, never by loading it);
- any other DLL there declaring a ValheimCLI plugin GUID (`valheimCLI.*`): ValheimCLI is one plugin;
- a pinned plugin in `BepInEx/scripts` when ScriptEngine is not pinned in `BepInEx/plugins` with `LoadOnStart = true`;
- a set that lacks a capability in the plan's `capabilities` or the runner's own, or offers it with a result version other than 1.

Mismatches name the file, the hashes found and expected, and the capabilities lost. A plugin that registers no extension commands is named as older than the required API. DLLs the manifest does not mention (the mods) are not judged.

`plan.CheckCliManifest(capabilities)` runs the file-set and capability check alone and returns a `CliManifestCheck` (the build, the installed files, the capabilities). It returns null, with nothing read, for an attached client, for the source of a disposable copy and for an in-place client without a manifest. `CliCapabilityManifest.Read(path).Check(install, capabilities)` does the same without a plan.

The report's provenance records which checks ran as `cliPreflight`:

| Client | `cliPreflight` |
|---|---|
| Owned | `static and live` |
| Attached | `live only: an attached client's files are its operator's` |
| Owned, in place, no `cliManifest` | `live only: static check not run: your install, run in place` |

A [targeted regression](sessions-targeted-regression.md)'s `regression.json` must name its `cli.manifest` (`valheim-test start` writes it), so its staged set is always checked.

## Generate a manifest

The toolkit hard-codes no ValheimCLI version. The pinned [ValheimCLI fork](../maintainers/valheimcli-release-contract.md) writes its build manifest with `CliBuildManifest.Write(build, dllPaths, output)`; `CliCapabilityManifest.Read(output)` validates it before the toolkit uses it.

`CliCapabilityManifest.Generate(build, dllPaths, listing)` is a convenience wrapper over the same reader. It hashes each DLL, reads its plugin GUIDs and reads the extensions from its IL: each `ExtensionRegistry.Register(id, version, apiVersion, new ExtensionCommand(name, ..., resultVersion), ...)` with literal names, as ValheimCLI's built-in commands register them. Registrations without literal names and console-module listings (`cli.standard/commands`) are not claimed; a registration whose shape it does not recognise is refused rather than guessed. Pass `listing`, the `cli_extensions` reply of a client that loaded exactly these files (from its `client-commands.jsonl`, for example), and the generator also requires every owner the files register to be live there with the same commands and versions.

`Write(path)` and `Read(path)` check the format. `Read` is the one place that decides a manifest describes a one-plugin build: an old split manifest (a core plus packs) is refused with `InvalidDataException` whichever command reads it, including a saved dependency lock's embedded manifest, which names the lock. `Generate` refuses such files the same way.

## Limits

The static check proves which files are installed, what their IL registers, and whether ScriptEngine is configured to load a pinned plugin in `scripts` at startup. It cannot prove that BepInEx loads them or that their registrations succeed; `CliCapabilities.Require` after launch decides. Generating from IL covers ValheimCLI's registration pattern, not every way a plugin could register an extension.

The live BepInEx check covers owned clients. An owned server fails its startup on an exit before BepInEx wrote its log, not on a missing line while it runs. A remote client opened by `HostedServerRun` runs the loader-file and Windows proxy checks before launch, not the other install checks; `InteractiveClient.StartAsync` called directly checks only its launch's required files.
