# Test-runtime hygiene and evidence rules

Traps that make a native test hang, fail for an unrelated reason, or pass without proving anything. Each item has a one-line reason and its source: a page on the [Valheim modding wiki](https://github.com/Valheim-Modding/Wiki/wiki), this toolkit's reference, or an issue. Game behaviour was checked against Valheim 1.0.16. A mod can link here instead of keeping its own copy of these rules.

## What the toolkit enforces

These rules hold whenever a run goes through the toolkit's runners with strict pins; a plan that opts out with `"pinning": "none"` gives up the world and plugin pins, and a hand-written driver that bypasses the runners has to keep the rules itself.

| Rule | Why | Enforced by |
|---|---|---|
| BepInEx core plus the plugins under test, and an empty `BepInEx/patchers` | A leftover preloader patcher breaks type loading before ValheimCLI listens ([#25](https://github.com/tvongaza/ValheimTesting/issues/25)) | `PinnedServerRun` and `ClientSession.Launch` refuse a patchers folder that is not the pinned one (`runtimePins`/`installPins` `patchers`), naming what it holds ([pinned server runner](packages/Valheim.Testing.GameSessions.md#pinned-server-runner)) |
| One runtime per process | A second process finds files locked and a preloader already running ([Server-Troubleshooting](https://github.com/Valheim-Modding/Wiki/wiki/Server-Troubleshooting#thunderstore-mod-manager-or-r2modman)) | `PinnedServerRun` runs each run's server from its own copy of the runtime; give each owned client its own install, since a client launches in place from `client.install` and nothing refuses two clients sharing one |
| Start the server executable, not `start_headless_server` | The stock script drops extra arguments, a mod manager's included | `GameLaunch.ForServer` starts `valheim_server` with the loader's variables ([dedicated server launch](packages/Valheim.Testing.Game.md#dedicated-server-launch)) |
| Pin the world; there is no `-seed` | The server replaces a missing or unloadable world with a new random one of the same name (1.0.16) | The strict `worlduid` pin and [`WorldFixture`](packages/Valheim.Testing.Game.md#actors-fixtures-and-reports); to test a seed, create the world in a client and copy it in as a hashed fixture |
| Devcommands on before joining | ValheimCLI refuses a mutating extension command, the join included, until devcommands is on | `SessionControl.Join` through `TestAccess` ([joining](packages/Valheim.Testing.Game.md#joining-protecting-and-placing-a-player)) |
| A first boot takes joins about 17 s after the world loads | The server opens its game socket only when location generation finishes; a join before that times out | `ClientRounds` waits for it; a scenario that joins itself calls `OwnedServerSession.WaitUntilJoinable` ([adapter session](packages/Valheim.Testing.Adapter.md#registration-and-the-session-capability)) |
| No teleport during the intro or within 2 s of a spawn or teleport | The game drops it silently | `PlayerPlacement.Arrive` waits inside the game until a teleport would be taken ([joining](packages/Valheim.Testing.Game.md#joining-protecting-and-placing-a-player)) |

What follows is what only you can do: how you write the mod, which characters you use, and what you count as evidence.

## Plugins

- **Only ever unpatch your own Harmony id.** `UnpatchAll()` without an id removes every mod's patches, and at shutdown that can leave world or character saves incomplete. HarmonyX logs "UnpatchAll has been called" when it happens, and the toolkit's teardown [log scan](packages/Valheim.Testing.Game.md#log-scan-at-teardown) fails the run on that line, but only after the run, by which time ValheimCLI's own patches may be gone. ([Best-Practices](https://github.com/Valheim-Modding/Wiki/wiki/Best-Practices#dont-use-unpatchall), [Client-Troubleshooting](https://github.com/Valheim-Modding/Wiki/wiki/Client-Troubleshooting#unpatchall))
- **Declare load order with `BepInDependency`.** It decides which plugin's `Awake` runs first. A test adapter declares its dependency on ValheimCLI (`valheimCLI.valheimCLI`) and on the mod it tests, so a reorder cannot run it before either exists. ([Best-Practices](https://github.com/Valheim-Modding/Wiki/wiki/Best-Practices#bepinex-dependencies-and-incompatibilities), [example adapter](../examples/FullLifecycle/MyMod.TestAdapter/Plugin.cs))
- **A plugin that is reloaded undoes everything in `OnDestroy`.** ScriptEngine destroys the old instance but its assembly stays loaded, so Harmony patches, event handlers, entries in shared collections, watchers, sockets, created GameObjects and console commands it leaves behind run old code next to the new build, and a reload test measures both. ValheimCLI keeps the checklist with its reload feature. ([What a plugin must undo in OnDestroy](https://github.com/tvongaza/valheimCLI/blob/80fb6cefcc99d7737ec9f9b589eff4d16e2fc5e3/docs/live-reload.md#what-a-plugin-must-undo-in-ondestroy), [ReloadCheck](../tools/reload-check/README.md))
- **Rebuild mods that embed ServerSync after a game update that touches it.** An outdated embedded copy shows up as a black screen after connecting, or as a `MissingFieldException`. ([Server-Troubleshooting](https://github.com/Valheim-Modding/Wiki/wiki/Server-Troubleshooting#blackscreen-after-connection-attempt), [Client-Troubleshooting](https://github.com/Valheim-Modding/Wiki/wiki/Client-Troubleshooting#missingfieldexception-field-fejdstartupm_connectionfailederror-not-found))

## Characters

- **Test characters are local, never Steam Cloud.** Tests move, protect and change them; a cloud copy carries that to every machine on the account, and cloud sync is itself a known source of save problems. ([Client-Troubleshooting](https://github.com/Valheim-Modding/Wiki/wiki/Client-Troubleshooting#cloud-saves), [FullLifecycle](../examples/FullLifecycle/README.md))
- **Cheat commands mark a character.** Turning devcommands on does not, but running any cheat command records it in the character's save, and the game then blocks achievements for that character (a modded game blocks them regardless). Hot patch 1.0.12 added a console command to lift the block, but a test character should simply be disposable and local, never someone's own. ([Valheim-1.0-FAQ](https://github.com/Valheim-Modding/Wiki/wiki/Valheim-1.0-FAQ#can-i-play-modded-and-get-steam-achievements), as the game does in 1.0.16)

## Timing

- **Wait for events, with timeouts that grow with the number of peers.** A dedicated server sends object updates to one peer per frame and starts each round after a 50 ms timer (1.0.16), so each extra client lengthens replication; vanilla stopped sending to every client on every loop, which is why a mod exists to restore it. ([Xbox-Compatible-Mods](https://github.com/Valheim-Modding/Wiki/wiki/Xbox-Compatible-Mods), [Waiting](packages/Valheim.Testing.Game.md#waiting))

## Evidence

- **An error-free log proves nothing about spawns or events.** A spawn or raid that never happens logs nothing (an enlarged spawn-blocking area stops raids silently), so such a claim needs a positive observation of the spawn. ([Client-Troubleshooting](https://github.com/Valheim-Modding/Wiki/wiki/Client-Troubleshooting#raids-do-not-spawn-enemies), [Spawning](https://github.com/Valheim-Modding/Wiki/wiki/Spawning))
- **Sample paint and height beside a zone seam, not on it.** A zone covers 64 one-metre cells but stores 65 vertices and paint texels per side, so the seam line exists in both neighbours and a sample exactly on it can read either copy. Put seam samples a metre inside each zone. (As the game does in 1.0.16: a zone's heights and paint mask are both (64+1)² samples; [ObserveCheck `paint`](../examples/ObserveCheck/README.md#paint-loaded-paint-channels) for sampling paint)
- **Keep the rest of the evidence rules.** Strict pins, one-shot mutations, incomplete is not zero, and one new output directory per attempt. ([agent workflow](agent-guide.md#use-the-apis-actual-contracts), [completion and handoff](agent-guide.md#completion-and-handoff))

## Builds and output

- **Use a fresh `NUGET_PACKAGES` per run when you rebuild an unpublished version number.** NuGet caches packages by id and version, so a rebuilt local-feed package with the same version restores the cached old copy. ([NuGet global packages folder](https://learn.microsoft.com/nuget/consume-packages/managing-the-global-packages-and-cache-folders), [package feeds](getting-started.md#package-versions-and-feeds))
- **Add `#:property PublishAot=false` to a file-based program that serializes JSON by reflection.** `dotnet run app.cs` defaults to Native AOT settings, which turn reflection-based `System.Text.Json` off, so `JsonSerializer.Serialize(object)` throws. `JsonDocument` or a source-generated context works either way. ([file-based apps](https://learn.microsoft.com/dotnet/core/sdk/file-based-apps), [reflection defaults under trimming and AOT](https://learn.microsoft.com/dotnet/standard/serialization/system-text-json/source-generation#disable-reflection-defaults))
- **Never zip a runner's output directory recursively.** `PinnedServerRun` keeps its copies of the runtime and world there for inspection; archive the reports and logs by name. ([pinned server runner](packages/Valheim.Testing.GameSessions.md#pinned-server-runner))

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
