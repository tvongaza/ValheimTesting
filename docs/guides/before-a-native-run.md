# Before a native run

Check the machine, the mod and your evidence rules before you start the real game.

## When to use this

Before any run that starts Valheim or a dedicated server, so it neither hangs nor passes without proving anything. A mod can link here instead of copying these rules.

## Steps

### 1. Check the machine

```sh
valheim-test env preflight
```

It checks this machine only and ends with `ELIGIBLE` or `REFUSED` and the reason. For a session on several machines, check its hosts instead:

```sh
valheim-test session check SESSION --hosts
```

It ends with `READY` or a `REFUSED` line per problem. A Linux dedicated server's profile hard-links the install's game files, as a Windows one does, so its runtime must be on the install's filesystem: both checks give a `profile storage` refusal for a runtime on another mount or disk, a bind mount, FAT or exFAT, or in a folder this user cannot write, and name where to put it ([where a profile lives](../reference/sessions-profiles.md#where-a-profile-lives)). A `game version` refusal names both installs when they are on different Valheim network versions and could not join: update the older one in Steam (or SteamCMD for a dedicated server). When it says a version is unknown, the reason follows: verify the install's files when its `assembly_valheim.dll` is missing or unreadable, check the host link when the read timed out, and run the check again when the file changed while it was read.

### 2. Leave the runtime to the toolkit

The runners enforce these rules; a hand-written driver must keep them itself.

| Rule | Why | Enforced by |
|---|---|---|
| A disposable profile per actor; the install is only read | A second process finds files locked and a preloader already running | Profiles and the host lock: one owned run per host |
| BepInEx core, the plugins under test, an empty `BepInEx/patchers` | A leftover preloader patcher breaks type loading before ValheimCLI listens | The `patchers` pin refuses any other folder |
| Start the server executable, not `start_headless_server` | The stock script drops extra arguments | `GameLaunch.ForServer` ([launch](../reference/game-launch.md#dedicated-server-launch)) |
| Pin the world; there is no `-seed` | The server replaces a missing world with a new random one | The strict `worlduid` pin and `WorldFixture` |
| Devcommands on before joining | ValheimCLI refuses a mutating command, the join included, until then | `SessionControl.Join` through `TestAccess` |
| Join only once the server's game socket is open | It opens after location generation finishes; an earlier join times out | `ClientRounds`, or `OwnedServerSession.WaitUntilJoinable` in your own scenario |
| No teleport during the intro or within 2 s of a spawn or teleport | The game drops it silently | `PlayerPlacement.Arrive` ([placing a player](../reference/game-player-placement.md)) |
| Your recent-servers list stays yours | The game adds every joined server to it | An owned client run moves it aside and puts it back |

To test a seed, create the world in a client and copy it in as a hashed fixture.

### 3. Check the mod

- **Unpatch only your own Harmony id.** `UnpatchAll()` without an id removes every mod's patches and can leave saves incomplete. The [log scan](../reference/game-log-scan.md) fails the run on it, but only at teardown. ([Best-Practices](https://github.com/Valheim-Modding/Wiki/wiki/Best-Practices#dont-use-unpatchall))
- **Declare load order with `BepInDependency`.** A test adapter depends on ValheimCLI (`valheimCLI.valheimCLI`) and on the mod it tests. ([example adapter](../../examples/FullLifecycle/ExampleMod.TestAdapter/Plugin.cs))
- **Undo everything in `OnDestroy` if the plugin is reloaded.** The old assembly stays loaded, so leftover patches, handlers and objects run next to the new build. ([what a plugin must undo](https://github.com/tvongaza/valheimCLI/blob/e596a7d984feaaa695f5c2999964b64e23fbfd92/docs/live-reload.md#what-a-plugin-must-undo-in-ondestroy), [ReloadCheck](../../tools/reload-check/README.md))
- **Rebuild a mod that embeds ServerSync after a game update that touches it.** An outdated copy shows as a black screen after connecting, or a `MissingFieldException`. ([Server-Troubleshooting](https://github.com/Valheim-Modding/Wiki/wiki/Server-Troubleshooting#blackscreen-after-connection-attempt))

### 4. Use a disposable character

- **Local, never Steam Cloud.** Tests change the character; a cloud copy carries that to every machine on the account. `valheim-test` stages its own.
- **Cheat commands mark a character.** Running a cheat records it in the save and blocks achievements for that character. Never use someone's own character. ([Valheim-1.0-FAQ](https://github.com/Valheim-Modding/Wiki/wiki/Valheim-1.0-FAQ#can-i-play-modded-and-get-steam-achievements))

### 5. Decide what counts as evidence

- **Wait for events, with timeouts that grow with the number of peers.** A dedicated server sends object updates to one peer per frame, so each client lengthens replication ([waiting](../reference/game-waiting.md#waiting)).
- **An error-free log proves nothing about spawns or events.** A raid that never happens logs nothing; claim it only from a positive observation. ([Spawning](https://github.com/Valheim-Modding/Wiki/wiki/Spawning))
- **Sample paint and height a metre inside each zone, not on the seam.** A zone stores 65 samples per side, so the seam exists in both neighbours ([ObserveCheck `paint`](../../examples/ObserveCheck/README.md#paint-loaded-paint-channels)).
- **Strict pins, one-shot mutations, incomplete is not zero, and one new output folder per attempt** ([reading results](../agent-guide.md#use-the-apis-actual-contracts)).
- **Archive reports and logs by name.** Never zip a run's output folder: it holds world copies, a server password and account identifiers.

## Check the result

After the run:

```sh
valheim-test env status
```

The last line reads `N runs ended and left nothing. Nothing was changed.` If it lists your run, follow [Clean up what a run left](debug-a-native-run.md#clean-up-what-a-run-left).

## Problems

| Observation | Next check |
|---|---|
| Breakpoints are not hit | Attach the debugger after the main menu loads ([Debugging-Plugins-via-IDE](https://github.com/Valheim-Modding/Wiki/wiki/Debugging-Plugins-via-IDE)) |
| A development-build player will not attach | Match the game's Unity version, which BepInEx logs at startup |
| Timings do not match a player's game | Time a Release build |
| A rebuilt local package restores the old copy | Use a new `NUGET_PACKAGES` folder per run ([local package feeds](../../CONTRIBUTING.md#try-an-unpublished-build-in-a-mod)) |
| `dotnet run app.cs` throws in `JsonSerializer.Serialize(object)` | Add `#:property PublishAot=false`, or use `JsonDocument` |

## Older wiki pages

Check these [Valheim modding wiki](https://github.com/Valheim-Modding/Wiki/wiki) pages' specifics against the current game.

| Page | What changed |
|---|---|
| [RPC-System-Reference-Sheet](https://github.com/Valheim-Modding/Wiki/wiki/RPC-System-Reference-Sheet) | A routed RPC takes up to 6 parameters after the sender, not 4 |
| [RPC-Method-registrations](https://github.com/Valheim-Modding/Wiki/wiki/RPC-Method-registrations) | Generated from an old game; regenerate names and hashes for the game you pin |
| [ZDO-Hashes](https://github.com/Valheim-Modding/Wiki/wiki/ZDO-Hashes) | Incomplete; the game keeps its keys in `ZDOVars` |
| [Spawning](https://github.com/Valheim-Modding/Wiki/wiki/Spawning), [Event-System](https://github.com/Valheim-Modding/Wiki/wiki/Event-System) | Lists predate later biomes and world modifiers |
| [Debugging-Plugins-via-IDE](https://github.com/Valheim-Modding/Wiki/wiki/Debugging-Plugins-via-IDE) | Names Unity 2022.3; the game runs on Unity 6 |
| [Custom-Item-and-Recipe-Creation](https://github.com/Valheim-Modding/Wiki/wiki/Custom-Item-and-Recipe-Creation), [Custom-Language---Localization](https://github.com/Valheim-Modding/Wiki/wiki/Custom-Language---Localization) | Built on ValheimLib |

## Related

- [Debug a native run](debug-a-native-run.md): read a failed run and clean up.
- [`valheim-test`](../packages/Valheim.Testing.NativeSmoke.md): every command and option.
