# Targeted native regression

**Audience:** a mod author checking one fix in the real game. **Concept:** A/B regression of a parent and a candidate build.

A copyable template for a small A/B regression in the real game: **one owned client hosts one disposable fixture world with one mod under test**, once with the parent build and once with the candidate. You fill in a short manifest, get a preflight result **without opening the game**, then run each arm through the toolkit's strict-pinned hosted [`ClientRounds`](../../docs/testing-toolkit.md#hosted-listen-server-worlds) runner. Restarts, dedicated servers and several clients belong in [FullLifecycle](../FullLifecycle/README.md) instead.

| File | What it is | Change it? |
|---|---|---|
| [Program.cs](Program.cs) | The `preflight`, `run` and `clean` commands | No; copy it as is |
| [Scenario.cs](Scenario.cs) | The rounds and assertions: everything that is the same on every machine | Yes: your mod's steps |
| [regression.sample.json](regression.sample.json) | The environment manifest: paths, port, character, which files are staged | Yes: copy it outside the repository and fill it in |

The sample scenario tests FullLifecycle's MyMod with its test adapter as the probe: its Harmony patches are applied, and `mymod_mark` marks the ground where the host stands with exactly one pole in the host's saved objects. Keep paths, ports and names out of `Scenario.cs`, so the source you ran is the source you can share.

The example pins the first published `Valheim.Testing.Game` package that contains `RegressionEnvironment` and `TargetedRegression` (`0.1.0-preview.17`); it has no source-project fallback. `scripts/consumer.cs` (run by `scripts/validate.cs` and at release) copies the example outside the checkout and builds it at that pin, restoring with NuGet.org as the only source into temporary package and HTTP caches that it removes afterward. The normal `dotnet run --project` commands below use your .NET environment; the later one-command bootstrap will apply the same owned-cache setup to those runs.

For a mod with several dependencies, use the [native dependency resolver](../../docs/native-dependency-resolution.md) to discover a pinned local set and the ValheimCLI packs that provide your scenario's commands. Review unresolved choices before filling this manifest; the resolver never treats a direct assembly reference as optional on its own.

## Which files come from where

| Source | Files | In the manifest |
|---|---|---|
| Valheim | The game install, run by Steam in your desktop session | `game` (only read) |
| BepInEx (BepInExPack_Valheim) | `BepInEx/core`, the Doorstop loader (`winhttp.dll` and `doorstop_config.ini` on Windows), `BepInEx/config/BepInEx.cfg` | Installed in `game`; copied as found |
| ValheimCLI | The core `valheimCLI.dll` and only the packs the scenario uses (`Valheim.Cli.Standard.dll` for the hosted session; `Valheim.Cli.WorldTools.dll` for world observations), all from one build, and that build's [capability manifest](../../docs/testing-toolkit.md#valheimcli-capability-manifest-preview-17) | `cli.core`, `cli.packs`, `cli.manifest` |
| The mod under test | Its parent and candidate builds, each with its source commit | `mod.arms` |
| The mod's dependencies | Every DLL the mod needs to load: the plugin each hard `[BepInDependency]` names, and libraries it references (for example JsonDotNET's detector plugin and `Newtonsoft.Json.dll`) | `plugins` |
| A test probe (optional) | A small plugin that serves the scenario's observations, such as MyMod.TestAdapter | `probe` |
| The fixture | A directory holding exactly one world, and that world's UID | `fixture` |
| Your character | A disposable **local** character, either already staged or registered in a disposable character store | `client.character`; optional `client.characterStore` and `client.steamUserDataDirectory` |

Every DLL is pinned by SHA256; leave a `sha256` empty and the preflight tells you the file's hash to review and paste. The prepared game's own plugins, scripts, patchers and configs are never copied.

## 1. Fill in the manifest

Copy `regression.sample.json` somewhere outside the repository and replace every `<...>`. Relative paths are relative to the manifest. `install` is a new directory: the tool creates it as a copy of `game` and owns it, and refuses any existing directory it did not create. For a basic smoke, use the pinned, game-created Valheim 1.0.16 world and character:

```csharp
var world = DefaultSmokeWorld.Prepare(Path.Combine(runRoot, "world-source"));
var character = DefaultSmokeCharacter.Ensure(Path.Combine(runRoot, "character-source"));
// regression.json: fixture.root = runRoot/world-source; fixture.worldUid = world.UidText
// client.character = DefaultSmokeCharacter.Name; client.characterStore = character.Root
// client.steamUserDataDirectory = the full path to Steam's userdata directory
```

Use a new `world-source` for each preparation. `Ensure` repairs a missing or stale character only in its own marked store, and refuses an unfamiliar file or personal character directory. The run stages that character only while its owned client is active, then removes its local file and backups. The fixture world is copied before the game edits it. A later game version needs a native load check; for a mod-specific world or character, supply your own registered fixture instead. The world source holds exactly one world, as the game saved it:

```text
<fixture root>/
  <WorldName>/
    _main.<n>.fwl2
    _main.<n>.db2
    ...
```

Read its UID from the world itself, never from its name: `WorldIdentity.Read(root).UidText`, or let the preflight tell you which UID the fixture holds. Name the arms however you like; `parent` and `candidate` are the convention. Two arms with the same SHA256 are refused unless you set `"repeatability": true` on purpose. Optional fields: `cli.manifest` (recommended; see step 6 below), `configs` (BepInEx config files by name; without `valheimCLI.valheimCLI.cfg` one is written with `client.port`), `patchers`, `optionalReferences` (assembly names a staged DLL only uses when present), `gamePins` (pin the prepared game's build and BepInEx core), `client.saveDirectory`, `client.startSeconds` and `client.joinSeconds`. `logScan` can name an exact, known BepInEx error header with a written reason, for example `"logScan": { "unknown-error": { "expected": ["[Error  :   BepInEx] Unable to start Unity log writer"], "reason": "Known log-writer limitation on this test client" } }`. New or different errors still fail the run; see the [log scan contract](../../docs/testing-toolkit.md#log-scan-at-teardown).

For a custom hosted character, register a **game-created** local save once in a [`DisposableCharacterStore`](../../docs/testing-toolkit.md#plan-rules-and-client-rounds-preview-13). Set `client.character` to its registered name, `client.characterStore` to that store's full path, and `client.steamUserDataDirectory` to Steam's `userdata` directory. Preflight checks the registration without touching `characters_local`. `run` refuses a name already in local or cloud saves, copies just that registered character before launch, and removes its file and game-made backups after the owned client stops, including on failure. The packaged default is for one client at a time: copies share its player ID.

## 2. Preflight, without the game

```sh
dotnet run --project examples/TargetedRegression -c Release -- preflight /absolute/path/to/regression.json
```

For each arm in turn it stages the disposable install and checks, before anything launches:

1. The fixture root holds one world with the manifest's UID. A world folder passed as the root, a `worlds_local` wrapper or several worlds are each named, with the tree above and the tree found.
2. Every file is its pinned SHA256. Each arm is first copied under its own artifact name (`parent-MyMod.dll`, `candidate-MyMod.dll`), and only the chosen one is installed as `mod.installAs`.
3. `BepInEx/plugins`, `patchers`, `config` and `scripts` are rebuilt from the manifest only: the ValheimCLI core and packs, `plugins`, the probe and one arm.
4. What each DLL **declares** decides, never its file name: every hard `[BepInDependency]` of every staged plugin is met by a staged `[BepInPlugin]` of that GUID and at least its minimum version; no staged plugin is `[BepInIncompatibility]` with another; every plugin loads in the client (`[BepInProcess]`); every referenced assembly is in the game, `BepInEx/core` or the manifest.
5. The disposable character is either present already or registered for run-time staging. The toolkit's own [owned-run preflight](../../docs/testing-toolkit.md#owned-run-preflight-preview-16) passes on the staged install: install pins, Doorstop loader, every pinned plugin installed exactly once, the fixture's pinned files.
6. With `cli.manifest`, the staged core and packs are exactly that build's set by SHA256 and provide every ValheimCLI command the run uses: the hosted rounds' session commands and the scenario's `Capabilities` owned by ValheimCLI (`valheim.*`, `cli.*`). A pack with the right name from another build, or a set without a command, is refused here instead of after the game starts. Without `cli.manifest` only the live check runs, and the run manifest says so. A probe's or the mod's own commands (any other owner) are checked live, before the first round.

It prints each arm's commit, artifact name and hashes and the allowlist with each plugin's MD5 and GUID: review them before the run. Exit 0 passed, 3 refused (the message names the field and the fix), 2 usage.

## 3. Run each arm

```sh
dotnet run --project examples/TargetedRegression -c Release -- run /absolute/path/to/regression.json parent /absolute/path/to/new-evidence-parent
dotnet run --project examples/TargetedRegression -c Release -- run /absolute/path/to/regression.json candidate /absolute/path/to/new-evidence-candidate
```

`run` stages the arm again and repeats every check, refuses any file that appeared in the staged folders since (a plugin outside the allowlist loads even when the test never calls it), then hands the plan to the hosted `ClientRounds`: it places the fixture, launches the owned client, requires the hosted-session capabilities and the scenario's `Capabilities` live, hosts the world with the character protected and runs `Scenario.Measure`. Every command is checked against strict pins: each staged plugin's GUID with its MD5 and the world's UID. No other plugin is pinned `absent`: the clean install loads nothing else, and strict pins refuse anything unlisted. The client is stopped and the world moved into the evidence in every outcome.

Each evidence directory holds `result.json`, `junit.xml`, `run-manifest.json` (the arm, every arm's commit and hash, the allowlist with SHA256 and MD5, the install pins, the world and capabilities; no machine path), the command trace `client-commands.jsonl`, the scenario's own JSON files and `host-world/`. `result.json` records the arm, the mod build and the cleanup steps in its provenance. The disposable install stays for the next arm; `clean` removes it.

## Review the result

Compare the arms by their `result.json`, never by an exit code alone: the parent should fail at the step the change fixes, the candidate should pass every step, and `run-manifest.json` should differ only in the mod's hash. Logs, saves and command traces can hold account identifiers and machine paths; keep them private.

## Share the result

From a checkout of this repository, make a public copy with the maintainer tool [tools/regression-bundle](../../tools/regression-bundle/README.md): it checks each arm's `result.json` against its `junit.xml`, command trace and `run-manifest.json`, then writes `Scenario.cs` and `Program.cs` exactly as they ran with a project pinned to the toolkit, a template of your manifest with placeholders and a generated A/B table, and refuses machine paths, private names and unrelated plugin names. It publishes nothing; you review the directory before sharing it.

## Limits

- One owned client hosting one world. A dedicated server, a second client and restarts of a server process are FullLifecycle's.
- The disposable install is a full copy of the game the first time (a few GB); it is reused while the game build, BepInEx core and the files at the game's root (its Doorstop loader) stay the same, and copied again when one changes.
- The optional registered-character path stages and removes one owned test character. Without it, stage your disposable local character yourself. The tool does not reserve a machine or publish anything.
- The metadata checks read what BepInEx reads; a dependency a mod finds by reflection at run time, or a config value it needs, is caught only by the live run.
