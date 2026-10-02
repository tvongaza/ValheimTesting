# Native mod load smoke (first slice of #156)

This command takes one or more selected client-side plugins into a new, disposable hosted Valheim world. It resolves their combined local BepInEx and assembly dependencies, chooses one pinned ValheimCLI build, stages a clean game-created world and character, launches an owned client with strict pins, and keeps private evidence. Success means that every selected plugin loaded and the client entered the fixture; it says nothing about a mod's reported gameplay bug.

For a step-by-step single-mod and mod-conflict investigation, including how to read failed setup versus failed gameplay, see [Debug a mod load or mod conflict](../../docs/debugging-mods.md).

```sh
dotnet run --project examples/NativeSmoke -c Release -- \
  --game /path/to/prepared/Valheim \
  --mod /path/to/MyMod.dll --source my-source-commit \
  --cli-manifest /path/to/cli-capabilities.json --cli-files /path/to/cli-build \
  --steam-userdata /path/to/Steam/userdata \
  --output /path/to/a/new/private-run
```

Repeat `--mod DLL` for a selected client-side pair. Repeat `--search-root DIR` for explicit local dependency trees. If an assembly reference is used only behind a disabled soft integration, repeat `--optional-reference ASSEMBLY` to confirm that deliberate omission; the dependency lock records each decision. Use `--loader-package FILE` for a captured BepInEx/Doorstop package, and `--port N` to change the ValheimCLI port (default 9500). If this particular install has a known benign BepInEx error, `--expected-log-error` accepts its **whole, exact header line** only when paired with `--expected-log-reason`; every other error still fails. Every input is read, never edited. The output directory must be new. Its `dependencies.lock.json` records unresolved choices even if setup stops; a ready run also keeps its manifest, source world and character, and `evidence/`. The owned game copy is removed at the end; an incomplete unmarked copy is left for inspection instead of being deleted blindly.

To compare two builds of the first selected mod, add `--compare-mod /path/to/NewBuild.dll --compare-source NEW_COMMIT`. The command runs `before` and `after` against the same world and companion DLLs, retaining a separate result for each. It refuses a changed companion, dependency, optional-reference decision or ValheimCLI build before launch. Both builds must declare the same plugin identities; this is a build comparison, not an add/remove-mod comparison. A failed first arm stops the comparison and keeps its evidence.

For a server-only mod, `server-load` can also launch an owned clean client and join it to the owned dedicated server:

```sh
dotnet run --project examples/NativeSmoke -c Release -- server-load \
  --server /path/to/prepared/dedicated-server \
  --mod /path/to/ServerMod.dll --adapter /path/to/NativeSmoke.SessionAdapter.dll \
  --client /path/to/prepared/client --steam-userdata /path/to/Steam/userdata \
  --cli-manifest /path/to/cli-capabilities.json --cli-files /path/to/cli-build \
  --output /path/to/a/new/private-server-run
```

The [test-only adapter](SessionAdapter/README.md) must be built against the exact game and ValheimCLI core. The command copies the dedicated runtime, clears plugins, scripts, configs and patchers **in the copy**, then stages the selected mods, their resolved dependencies, ValheimCLI and the adapter. With `--client`, it copies a separate client install containing ValheimCLI alone, stages the clean local character for that run, and requires the client to join with every selected server plugin pinned absent. Both owned processes and the staged character are cleaned up on success or failure. Without `--client`, it only checks server load and socket readiness, printing `SERVER_LOAD_PASS` rather than a client-join pass. This load smoke does not request admin-only player protection.

Repeat `--mod`, `--search-root` and `--optional-reference` as above. Use `--config FILE` for chosen server settings, such as disabling expensive world generation when it is irrelevant to the setup check. Use `--plugin-file FILE` and `--plugin-dir DIR` for a mod's external assets beside its DLL in `BepInEx/plugins`. The staged configs and asset bytes enter the strict runtime manifest; DLLs inside asset directories are refused so they cannot bypass dependency resolution. The output contains private plans and evidence, including a password in `plan.json`; do not publish it.

To isolate a load interaction, change `server-load` to `server-load-ab` and add `--remove-mod /path/to/SecondMod.dll`. The command resolves both sets before launch, runs the full set in `before/`, then runs the same fixture without that one mod in `after/`. It keeps the surviving mod files, ValheimCLI, server/client installs, configs and selected assets pinned between arms; dependencies used only by the removed mod may disappear. A native failure in the full-set arm still runs the removal arm and keeps both results; an input/setup refusal stops before the second arm. A difference narrows a load interaction but does not assign fault to a mod. Both arms are load/join checks, so use small fixture plugins for routine runner validation and reserve expensive real-mod generation for a specific regression.

The packaged world and character were made by Valheim 1.0.16 and checked in a native client. Recheck them with a new game version. Each copy of the character has the same player ID, so run only one client with it at a time.

On a Windows 1.0.16 client, the command passed 12/12 steps with one selected example mod and 12/12 with that mod plus its test adapter. Both runs used strict pins, entered the packaged hosted world, and left no character, world copy or owned install behind. The station's pre-existing saves and launcher matched their before-run hashes. The first arm failed solely on a known BepInEx Unity-log-writer line, so the passing arms named that exact line and a reason; no general error suppression was added.

The dedicated-server path also passed a strict 17-step Windows 1.0.16 run with two selected server mods and a clean joined client containing neither mod. That is setup and interoperability evidence, not a gameplay result; use fast fixture mods for routine runner checks rather than repeating a long catalogue or world generation.

This example still builds against the library source in this branch. Before closing #156, generate and build a consumer from the released `Valheim.Testing.Game` package and finish the remaining external-mod setup cases. Add/remove-mod A/B remains in #158. Keep the generated manifest and game logs private; they may contain local paths or account identifiers.
