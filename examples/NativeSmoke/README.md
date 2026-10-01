# Native mod load smoke (first slice of #156)

This command takes one or more selected client-side plugins into a new, disposable hosted Valheim world. It resolves their combined local BepInEx and assembly dependencies, chooses one pinned ValheimCLI build, stages a clean game-created world and character, launches an owned client with strict pins, and keeps private evidence. Success means that every selected plugin loaded and the client entered the fixture; it says nothing about a mod's reported gameplay bug.

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

The dedicated-server **load half** is available as `server-load` while the clean-client join is being built:

```sh
dotnet run --project examples/NativeSmoke -c Release -- server-load \
  --server /path/to/prepared/dedicated-server \
  --mod /path/to/ServerMod.dll --adapter /path/to/NativeSmoke.SessionAdapter.dll \
  --cli-manifest /path/to/cli-capabilities.json --cli-files /path/to/cli-build \
  --output /path/to/a/new/private-server-run
```

The adapter is [test-only](SessionAdapter/README.md) and built against the exact game and ValheimCLI core. `server-load` copies the dedicated runtime, removes any source plugins, scripts, configs and patchers **in the copy**, stages only the selected mods, their resolved dependencies, ValheimCLI and adapter, and runs the pinned server on a second owned copy. It checks the world UID, each plugin's exact hash, the owned process session and whether the game socket is accepting connections. It prints `SERVER_LOAD_PASS` explicitly to distinguish this from a client-join pass. Repeat `--mod`, `--search-root` and `--optional-reference` as above. The output keeps private plans and evidence; the password in `plan.json` must not be published.

The packaged world and character were made by Valheim 1.0.16 and checked in a native client. Recheck them with a new game version. Each copy of the character has the same player ID, so run only one client with it at a time.

On a Windows 1.0.16 client, the command passed 12/12 steps with one selected example mod and 12/12 with that mod plus its test adapter. Both runs used strict pins, entered the packaged hosted world, and left no character, world copy or owned install behind. The station's pre-existing saves and launcher matched their before-run hashes. The first arm failed solely on a known BepInEx Unity-log-writer line, so the passing arms named that exact line and a reason; no general error suppression was added.

This first slice still builds against the library source in this branch. Before closing #156, switch it to the released `Valheim.Testing.Game` package, join the dedicated server from a clean client, and verify the command with the external-mod cases in the issue. The add/remove-mod A/B mode and server-only pairing remain in #158. Keep the generated manifest and game logs private; they may contain local paths or account identifiers.
