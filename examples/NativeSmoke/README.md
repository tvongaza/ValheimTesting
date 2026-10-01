# Native mod load smoke (first slice of #156)

This command takes a selected client-side plugin into a new, disposable hosted Valheim world. It resolves local BepInEx and assembly dependencies, chooses one pinned ValheimCLI build, stages a clean game-created world and character, launches an owned client with strict pins, and keeps private evidence. Success means that the selected plugin loaded and the client entered the fixture; it says nothing about a mod's reported gameplay bug.

```sh
dotnet run --project examples/NativeSmoke -c Release -- \
  --game /path/to/prepared/Valheim \
  --mod /path/to/MyMod.dll --source my-source-commit \
  --cli-manifest /path/to/cli-capabilities.json --cli-files /path/to/cli-build \
  --steam-userdata /path/to/Steam/userdata \
  --output /path/to/a/new/private-run
```

Use `--search-root DIR` for one local dependency tree, `--loader-package FILE` for a captured BepInEx/Doorstop package, and `--port N` to change the ValheimCLI port (default 9500). Every input is read, never edited. The output directory must be new. Its `dependencies.lock.json` records unresolved choices even if setup stops; a ready run also keeps its manifest, source world and character, and `evidence/`. The owned game copy is removed at the end; an incomplete unmarked copy is left for inspection instead of being deleted blindly.

The packaged world and character were made by Valheim 1.0.16 and checked in a native client. Recheck them with a new game version. Each copy of the character has the same player ID, so run only one client with it at a time.

This first slice still builds against the library source in this branch. Before closing #156, switch it to the released `Valheim.Testing.Game` package, add an owned dedicated-server path and complete the multi-mod cases in #158, and verify the command with the external-mod cases in the issue. Keep the generated manifest and game logs private; they may contain local paths or account identifiers.
