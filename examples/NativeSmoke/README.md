# Disposable native mod load smoke

This command takes one or more selected client-side plugins into a new, disposable hosted Valheim world. It resolves their combined local BepInEx and assembly dependencies, chooses one pinned ValheimCLI build, stages a clean game-created world and character, launches an owned client with strict pins, and keeps private evidence. Success means that every selected plugin loaded and the client entered the fixture; it says nothing about a mod's reported gameplay bug.

For a step-by-step single-mod and mod-conflict investigation, including how to read failed setup versus failed gameplay, see [Debug a mod load or mod conflict](../../docs/debugging-mods.md).

Install the preview command-line tool from NuGet.org, then run it against a prepared game install. `init` can first create an editable project without launching the game. The tool checks that its required `Valheim.Testing.Game` package is published and builds the generated project from NuGet.org alone before launching.

```sh
dotnet tool install --global Valheim.Testing.NativeSmoke --prerelease
valheim-test start \
  --game /path/to/prepared/Valheim \
  --mod /path/to/MyMod.dll \
  --output /path/to/a/new/private-run
```

The selected game must already have a coherent BepInEx/Doorstop loader, or use `--loader-package` with a [reviewed extracted loader set](../../docs/bepinex-loader-package.md). It also needs one ValheimCLI core-and-pack bundle: the tool discovers a single capability manifest under `BepInEx/plugins` or `BepInEx/scripts`; use `VALHEIMCLI_BUNDLE` or `--cli-manifest` with `--cli-files` if none or several are present. It never silently downloads or mixes plugin builds. It selects the platform's Steam userdata directory only when that choice is unique; otherwise give `--steam-userdata`. On Homebrew macOS installs, a global .NET tool may need `DOTNET_ROOT` set to the directory reported by `dotnet --info` before its apphost launches. The tool requires the .NET 10 SDK for its generated project.

`valheim-test init --output NEW_DIR` creates the hosted consumer alone; `valheim-test init server --output NEW_DIR` creates the server consumer. A successful run also places `consumer/` beside its private plan and evidence, ready for focused assertions. The tool refuses an unpublished Game API before writing a consumer project.

Repeat `--mod DLL` for a selected client-side pair. Repeat `--search-root DIR` for explicit local dependency trees. Byte-identical copies of a dependency with the same assembly and plugin identity count as one choice; the lock records every discovered source path and stages just one. Different builds still require an explicit choice. If an assembly reference is used only behind a disabled soft integration, repeat `--optional-reference ASSEMBLY` to confirm that deliberate omission; the dependency lock records each decision. Use `--loader-package FILE` for a captured BepInEx/Doorstop package, and `--port N` to change the ValheimCLI port (default 9500). If this particular install has a known benign BepInEx error, `--expected-log-error` accepts its **whole, exact header line** only when paired with `--expected-log-reason`; every other error still fails. Every input is read, never edited. The output directory must be new. Its `dependencies.lock.json` records unresolved choices even if setup stops; a ready run also keeps its manifest, source world and character, and `evidence/`. The owned game copy is removed at the end; an incomplete unmarked copy is left for inspection instead of being deleted blindly.

To compare two builds of the first selected mod, add `--compare-mod /path/to/NewBuild.dll --compare-source NEW_COMMIT`. The command runs `before` and `after` against the same world and companion DLLs, retaining a separate result for each. It refuses a changed companion, dependency, optional-reference decision or ValheimCLI build before launch. Both builds must declare the same plugin identities; this is a build comparison, not an add/remove-mod comparison. A failed first arm stops the comparison and keeps its evidence.

For a server-only mod, `server-load` can also launch an owned clean client and join it to the owned dedicated server:

```sh
valheim-test server-load \
  --server /path/to/prepared/dedicated-server \
  --mod /path/to/ServerMod.dll \
  --client /path/to/prepared/client \
  --output /path/to/a/new/private-server-run
```

The source server and client may instead be unmodded installs. Supply reviewed, extracted loader manifests for each platform and one coherent ValheimCLI bundle explicitly:

```sh
valheim-test server-load \
  --server /path/to/unmodded/dedicated-server \
  --client /path/to/unmodded/client \
  --loader-package /private/server-loader.json \
  --client-loader-package /private/client-loader.json \
  --cli-manifest /private/ValheimCLI/cli-capabilities.json \
  --cli-files /private/ValheimCLI \
  --mod /path/to/ServerMod.dll \
  --output /private/runs/server-load-1
```

`--loader-package` selects the server's BepInEx core for dependency resolution and adapter compilation. The client package is separate because a dedicated server's loader files are not assumed to work in the client. Both packages are pinned and applied only to the disposable copies; the result records their identities. The command still needs a compatible game build and local, reviewed loader and ValheimCLI files. It does not download or choose those for you. `server-load-ab` accepts the same options and holds both packages fixed between arms.

The command builds its [test-only adapter](SessionAdapter/README.md) from source embedded in the tool against the exact game and ValheimCLI core it selected. `--adapter DLL` can override this when an independently built adapter is required. The command copies the dedicated runtime, clears plugins, scripts, configs and patchers **in the copy**, then stages the selected mods, their resolved dependencies, ValheimCLI and the adapter. With `--client`, it copies a separate client install containing ValheimCLI alone, stages the clean local character for that run, and requires the client to join with every selected server plugin pinned absent. Both owned processes and the staged character are cleaned up on success or failure. Without `--client`, it only checks server load and socket readiness, printing `SERVER_LOAD_PASS` rather than a client-join pass. This load smoke does not request admin-only player protection. Its disposable server plan waits at most 20 seconds to quit before a recorded kill; it makes no save-on-quit or crossplay-retirement claim. Use the full server runner and its shutdown assertions for those claims.

Repeat `--mod`, `--search-root` and `--optional-reference` as above. Use `--config FILE` for chosen server settings, such as disabling expensive world generation when it is irrelevant to the setup check. Use `--plugin-file FILE` and `--plugin-dir DIR` for a mod's external assets beside its DLL in `BepInEx/plugins`. The staged configs and asset bytes enter the strict runtime manifest; DLLs inside asset directories are refused so they cannot bypass dependency resolution. The output contains private plans and evidence, including a password in `plan.json`; do not publish it.

Disk use: a `server-load` holds the staged server, the run's own copy of it and, with `--client`, the staged clean client at once, about 2 GB per server copy and 4 GB per client on Windows. It refuses before the first copy when `--output`'s drive lacks that room plus headroom. When it ends, the staged copies are removed, and the run removes its runtime copy after keeping what the run wrote in `evidence/runtime-changes/` ([what the output keeps](../../docs/testing-toolkit.md#pinned-server-runner)). So a finished run keeps megabytes, not gigabytes, and the final line prints the evidence's size. Set `VALHEIM_TESTING_KEEP_RUNTIME=1` to keep the run's whole runtime copy for debugging. `valheim-test copies DIR` lists the game copies earlier or interrupted runs left under DIR: their kind, size, age, whether a process uses them, and their run's result. It changes nothing. `valheim-test copies DIR --remove COPY` removes only the copies named, after keeping what each run changed ([copies left behind](../../docs/testing-toolkit.md#pinned-server-runner)).

To isolate a load interaction, change `server-load` to `server-load-ab` and add `--remove-mod /path/to/SecondMod.dll`. The command resolves both sets before launch, runs the full set in `before/`, then runs the same fixture without that one mod in `after/`. It keeps the surviving mod files, ValheimCLI, server/client installs, configs and selected assets pinned between arms; dependencies used only by the removed mod may disappear. A native failure in the full-set arm still runs the removal arm and keeps both results; an input/setup refusal stops before the second arm. A difference narrows a load interaction but does not assign fault to a mod. Both arms are load/join checks, so use small fixture plugins for routine runner validation and reserve expensive real-mod generation for a specific regression.

The packaged world and character were made by Valheim 1.0.16 and checked in a native client. Recheck them with a new game version. Each copy of the character has the same player ID, so run only one client with it at a time.

On a Windows 1.0.16 client, the command passed 12/12 steps with one selected example mod and 12/12 with that mod plus its test adapter. Both runs used strict pins, entered the packaged hosted world, and left no character, world copy or owned install behind. The station's pre-existing saves and launcher matched their before-run hashes. The first arm failed solely on a known BepInEx Unity-log-writer line, so the passing arms named that exact line and a reason; no general error suppression was added.

The dedicated-server path also passed a strict 17-step Windows 1.0.16 run with two selected server mods and a clean joined client containing neither mod. That is setup and interoperability evidence, not a gameplay result; use fast fixture mods for routine runner checks rather than repeating a long catalogue or world generation.

The tool package depends on the published `Valheim.Testing.Game` API; its generated consumer has a package reference rather than a project reference. Native setup/load results do not establish the mod's gameplay behavior. Keep the generated manifest and game logs private; they may contain local paths or account identifiers.
