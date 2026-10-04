# Test-runtime hygiene and evidence rules

A checklist of traps that make a native test hang, fail for an unrelated reason, or pass without proving anything. Each item has a one-line reason and its source: a page on the [Valheim modding wiki](https://github.com/Valheim-Modding/Wiki/wiki), this toolkit's reference, or an issue. Game behaviour was checked against Valheim 1.0.16. A mod can link here instead of keeping its own copy of these rules.

## The runtime

- **BepInEx core plus the plugins under test, and an empty `BepInEx/patchers`.** A preloader patcher left behind by a removed mod can break type loading so that ValheimCLI never starts listening, and startup waits out its whole deadline instead of failing with the reason. ([#25](https://github.com/tvongaza/ValheimTesting/issues/25))
- **One runtime per process.** A client and a server on one machine must not share a mod-manager profile: the second process finds files locked and a BepInEx preloader already running. `PinnedServerRun` copies the runtime for each run. ([Server-Troubleshooting](https://github.com/Valheim-Modding/Wiki/wiki/Server-Troubleshooting#thunderstore-mod-manager-or-r2modman), [pinned server runner](testing-toolkit.md#pinned-server-runner))
- **Start the server executable, not `start_headless_server`.** The stock start script drops any extra arguments, including a mod manager's, unless edited. `ServerLaunch` starts `valheim_server` directly with the variables BepInEx's loader needs. ([Server-Troubleshooting](https://github.com/Valheim-Modding/Wiki/wiki/Server-Troubleshooting#thunderstore-mod-manager-or-r2modman), [Linux dedicated server](testing-toolkit.md#linux-dedicated-server-preview-11))
- **Pin a world file; there is no `-seed`.** The dedicated server takes a world name, not a seed, and creates a missing world with a random seed. A world that exists but fails to load is replaced the same way, by a new world with the same name and a new seed; pin the world UID so that is refused. To test a particular seed, create the world in a game client and copy it in as a hashed fixture. (As the game does in 1.0.16; [`WorldFixture`](testing-toolkit.md#actors-fixtures-and-evidence))

## Plugins

- **Declare load order with `BepInDependency`.** It decides which plugin's `Awake` runs first. A test adapter declares its dependency on ValheimCLI (`valheimCLI.valheimCLI`) and on the mod it tests, so a reorder cannot run it before either exists. ([Best-Practices](https://github.com/Valheim-Modding/Wiki/wiki/Best-Practices#bepinex-dependencies-and-incompatibilities), [example adapter](../examples/FullLifecycle/MyMod.TestAdapter/Plugin.cs))
- **Only ever unpatch your own Harmony id.** `UnpatchAll()` without an id removes every mod's patches, and at shutdown that can leave world or character saves incomplete. HarmonyX logs "UnpatchAll has been called" when it happens. ([Best-Practices](https://github.com/Valheim-Modding/Wiki/wiki/Best-Practices#dont-use-unpatchall), [Client-Troubleshooting](https://github.com/Valheim-Modding/Wiki/wiki/Client-Troubleshooting#unpatchall))
- **A plugin that is reloaded undoes everything in `OnDestroy`.** ScriptEngine destroys the old instance but its assembly stays loaded, so Harmony patches, event handlers, entries in shared collections, watchers, sockets, created GameObjects and console commands it leaves behind run old code next to the new build, and a reload test measures both. ValheimCLI keeps the checklist with its reload feature. ([What a plugin must undo in OnDestroy](https://github.com/tvongaza/valheimCLI/blob/review/cli-command-packs-ready/docs/live-reload.md#what-a-plugin-must-undo-in-ondestroy), [ReloadCheck](../tools/reload-check/README.md))
- **Rebuild mods that embed ServerSync after a game update that touches it.** An outdated embedded copy shows up as a black screen after connecting, or as a `MissingFieldException`. ([Server-Troubleshooting](https://github.com/Valheim-Modding/Wiki/wiki/Server-Troubleshooting#blackscreen-after-connection-attempt), [Client-Troubleshooting](https://github.com/Valheim-Modding/Wiki/wiki/Client-Troubleshooting#missingfieldexception-field-fejdstartupm_connectionfailederror-not-found))

## Characters

- **Test characters are local, never Steam Cloud.** Tests move, protect and change them; a cloud copy carries that to every machine on the account, and cloud sync is itself a known source of save problems. ([Client-Troubleshooting](https://github.com/Valheim-Modding/Wiki/wiki/Client-Troubleshooting#cloud-saves), [FullLifecycle](../examples/FullLifecycle/README.md))
- **Cheat commands mark a character.** Turning devcommands on does not, but running any cheat command records it in the character's save, and the game then blocks achievements for that character (a modded game blocks them regardless). Hot patch 1.0.12 added a console command to lift the block, but a test character should simply be disposable and local, never someone's own. ([Valheim-1.0-FAQ](https://github.com/Valheim-Modding/Wiki/wiki/Valheim-1.0-FAQ#can-i-play-modded-and-get-steam-achievements), as the game does in 1.0.16)
- **Turn devcommands on before joining.** ValheimCLI refuses a mutating extension command, the join included, until devcommands is on; `SessionControl.Join` turns it on first. ([SessionControl.Join](testing-toolkit.md#native-terrain-replicated-to-a-valheimcli-only-client-preview-3))

## Timing

- **A first boot takes joins about 17 s after the world loads.** The dedicated server opens its game socket only when location generation finishes (at once for a world whose locations were generated before), and a join before that times out. Call `OwnedServerSession.WaitUntilJoinable` just before the first join. ([adapter helpers](testing-toolkit.md#game-side-adapter-helpers-valheimtestingadapter-preview-1))
- **No teleport during the intro or within 2 s of a spawn or of a previous teleport.** The game drops it silently. `PlayerPlacement.Arrive` skips the intro, waits inside the game until a teleport would be taken (`cli_wait_teleportable`), teleports once and waits inside the game for the finished teleport and the player's supported landing. ([PlayerPlacement](testing-toolkit.md#native-terrain-replicated-to-a-valheimcli-only-client-preview-3))
- **Wait for events, with timeouts that grow with the number of peers.** A dedicated server sends object updates to one peer per frame and starts each round after a 50 ms timer (1.0.16), so each extra client lengthens replication; vanilla stopped sending to every client on every loop, which is why a mod exists to restore it. ([Xbox-Compatible-Mods](https://github.com/Valheim-Modding/Wiki/wiki/Xbox-Compatible-Mods), [Waiting](testing-toolkit.md#waiting))

## Evidence

- **An error-free log proves nothing about spawns or events.** A spawn or raid that never happens logs nothing (an enlarged spawn-blocking area stops raids silently), so such a claim needs a positive observation of the spawn. ([Client-Troubleshooting](https://github.com/Valheim-Modding/Wiki/wiki/Client-Troubleshooting#raids-do-not-spawn-enemies), [Spawning](https://github.com/Valheim-Modding/Wiki/wiki/Spawning))
- **Sample paint and height beside a zone seam, not on it.** A zone covers 64 one-metre cells but stores 65 vertices and paint texels per side, so the seam line exists in both neighbours and a sample exactly on it can read either copy. Put seam samples a metre inside each zone. (As the game does in 1.0.16: a zone's heights and paint mask are both (64+1)² samples; [ObserveCheck `paint`](../examples/ObserveCheck/README.md#paint-loaded-paint-channels) for sampling paint)
- **Keep the rest of the evidence rules.** Strict pins, one-shot mutations, incomplete is not zero, and one new output directory per attempt. ([agent workflow](agent-guide.md#use-the-apis-actual-contracts), [completion and handoff](agent-guide.md#completion-and-handoff))

## Builds and output

- **Use a fresh `NUGET_PACKAGES` per run when you rebuild an unpublished version number.** NuGet caches packages by id and version, so a rebuilt local-feed package with the same version restores the cached old copy. ([NuGet global packages folder](https://learn.microsoft.com/nuget/consume-packages/managing-the-global-packages-and-cache-folders), [package feeds](getting-started.md#package-versions-and-feeds))
- **Add `#:property PublishAot=false` to a file-based program that serializes JSON by reflection.** `dotnet run app.cs` defaults to Native AOT settings, which turn reflection-based `System.Text.Json` off, so `JsonSerializer.Serialize(object)` throws. `JsonDocument` or a source-generated context works either way. ([file-based apps](https://learn.microsoft.com/dotnet/core/sdk/file-based-apps), [reflection defaults under trimming and AOT](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/source-generation#disable-reflection-defaults))
- **Never zip a runner's output directory recursively.** `PinnedServerRun` keeps its copies of the runtime and world there for inspection; archive the reports and logs by name. ([pinned server runner](testing-toolkit.md#pinned-server-runner))

## Debugging and profiling

- **Attach the debugger after the main menu has loaded.** Attaching earlier is a common reason breakpoints are not hit. ([Debugging-Plugins-via-IDE](https://github.com/Valheim-Modding/Wiki/wiki/Debugging-Plugins-via-IDE))
- **A development-build player must match the game's Unity version.** BepInEx logs that version at startup; 1.0 runs on Unity 6 (6000.0.75f1), not the 2022.3 release the debugging page names. ([Debugging-Plugins-via-IDE](https://github.com/Valheim-Modding/Wiki/wiki/Debugging-Plugins-via-IDE), [Valheim-Unity-Project-Guide](https://github.com/Valheim-Modding/Wiki/wiki/Valheim-Unity-Project-Guide))
- **Give the profiler your mod's symbols.** Without them it cannot attribute time to your code. ([Profiling-Performance](https://github.com/Valheim-Modding/Wiki/wiki/Profiling-Performance))
- **Time Release builds.** A Debug build is compiled without optimisation, so its timings do not predict a player's game. ([Debug and Release configurations](https://learn.microsoft.com/visualstudio/debugger/how-to-set-debug-and-release-configurations))

## Wiki pages that predate 1.0

These pages are still useful for how a system works; check their specifics against 1.0.

| Page | What changed |
|---|---|
| [RPC-System-Reference-Sheet](https://github.com/Valheim-Modding/Wiki/wiki/RPC-System-Reference-Sheet) | A routed RPC takes up to 6 parameters after the sender in 1.0.16, not 4 |
| [RPC-Method-registrations](https://github.com/Valheim-Modding/Wiki/wiki/RPC-Method-registrations) | Generated from 0.221.12; regenerate names and hashes for the game you pin |
| [ZDO-Hashes](https://github.com/Valheim-Modding/Wiki/wiki/ZDO-Hashes) | Incomplete by its own account; 1.0 code keeps its keys in `ZDOVars` |
| [Spawning](https://github.com/Valheim-Modding/Wiki/wiki/Spawning), [Event-System](https://github.com/Valheim-Modding/Wiki/wiki/Event-System) | Location and event lists predate later biomes and world modifiers |
| [Debugging-Plugins-via-IDE](https://github.com/Valheim-Modding/Wiki/wiki/Debugging-Plugins-via-IDE) | Names Unity 2022.3.50; 1.0 is on Unity 6 |
| [Custom-Item-and-Recipe-Creation](https://github.com/Valheim-Modding/Wiki/wiki/Custom-Item-and-Recipe-Creation), [Custom-Language---Localization](https://github.com/Valheim-Modding/Wiki/wiki/Custom-Language---Localization) | Built on ValheimLib; the item page also targets Unity 2019 |

[Valheim-1.0-FAQ](https://github.com/Valheim-Modding/Wiki/wiki/Valheim-1.0-FAQ), [Client-Troubleshooting](https://github.com/Valheim-Modding/Wiki/wiki/Client-Troubleshooting) and [Valheim-Unity-Project-Guide](https://github.com/Valheim-Modding/Wiki/wiki/Valheim-Unity-Project-Guide) are current for 1.0. The FAQ also covers the `default_pre1_0` and `default_old` branches for running an older game build.
