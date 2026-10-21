# Disposable profiles

Every owned game process runs from a disposable loader profile: the install's game files, a loader the run chose, and the run's plugins. The install itself is only read, and the toolkit never copies the game.

[Profiles and hosts](../concepts/profiles-and-hosts.md) explains the idea. This page is the contract.

## Who runs from a profile

| Actor | Profile |
|---|---|
| An owned client on this machine (`session.OpenClient`, `ClientActor.OnThisMachine`) | Yes, unless it runs [in place](#run-an-install-in-place) |
| A standalone `PinnedServerRun` server, here or with `--inventory` | Yes |
| Every actor of a campaign, which `server-load` and `server-load-ab` prepare | Yes |
| Each `TargetedRegression` arm | Yes, a fresh one per arm |
| An attached client | No: its operator's install, never read |

A client that leaves and rejoins between rounds reuses its profile.

## What a profile holds

| OS | The game runs from |
|---|---|
| Windows (client and server), Linux (server), macOS (server) | The profile's launch folder, which hard-links the install's launch files |
| macOS (client) | The install's own app bundle, with Doorstop pointed at the profile |

A linked launch folder is the game's root for the run: `Paths.GameRootPath`, BepInEx's `preloader_*.log` and any file a mod writes there land in the profile, not the install. The Mac dedicated server's files are linked two folders down, at `<profile>/game/server/`: BepInEx on macOS takes the game root as the executable's fourth parent folder (it expects `Valheim.app/Contents/MacOS/Valheim`), so with the executable at `<profile>/game/server/valheim_server/Valheim` that is the profile itself, where its loader is. Run from its install, the same server's game root is two folders above the install. A macOS client cannot be linked: a bundle started from another path fails Gatekeeper's check, so a Mac client's profile starts the source bundle. A write beside a Mac client's executable can reach the install.

The launch folder hard-links only these:

| Linked | Windows | Linux | macOS server |
|---|---|---|---|
| The executable | `valheim.exe` or `valheim_server.exe` | `valheim_server.x86_64` | `valheim_server/Valheim` |
| Its data | The matching `valheim_Data/**` or `valheim_server_Data/**` | `valheim_server_Data/**` | The rest of `valheim_server/**`: its `*.dylib`, `Data/**`, `MonoBleedingEdge/**` and `PlugIns/**` |
| Other trees | `MonoBleedingEdge/**`, `D3D12/**` | `linux64/**` (`steamclient.so`) | None |
| Root files | `UnityCrashHandler64.exe`, `UnityPlayer.dll`, `steam_api64.dll`, `steamclient64.dll`, `steamwebrtc64.dll`, `tier0_s64.dll`, `vstdlib_s64.dll` | `UnityPlayer.so`, `libparty.so` | None |

Everything else stays out: BepInEx, Doorstop, `logs/`, text and log output, crash dumps, `steam_appid.txt`, start scripts (`start_server.command` on a Mac), the manual, Steam's `steamapps/` and `EmptySteamDepot/`, Finder's `.DS_Store`, mod-manager proxies and unknown root libraries. A left-out root file is recorded as `<actor>ProfileLeftOut` in the provenance, with its reason. An unknown root DLL's or `.so`'s reason says to add it to the game's list if a launch then fails during start-up.

A hard link is the install's file under a second name. A new file lands in the profile, but a write that overwrites a linked game file in place changes the install too.

On a container host whose install is in the image, a hard link to a file of a lower overlayfs layer copies that file up into the container's own layer first. The link then costs the file's size in the container, and the image's install is never touched.

Into the profile, the run stages:

| Part | From |
|---|---|
| The loader | The pinned BepInExPack_Valheim on Windows and Linux; the install's native loader on macOS; or the environment's `loaderPackage` ([pin a loader](game-run-inputs.md#pin-a-bepinex-loader)) |
| `BepInEx/config/BepInEx.cfg` | An explicitly selected config, else the loader package's, else the source install's |
| ValheimCLI | The toolkit's pinned set, replacing any set in the install |
| Plugins | The run's selection: a dependency lock, `--mod` and its dependencies, or a regression's allowlist |

If a loader package has no `BepInEx.cfg`, a macOS profile keeps the source file and a Windows or Linux profile has none, so BepInEx writes its defaults. The staged config is checked against its source hash.

## Where a profile lives

A profile lives under its environment's `runtime` folder, in a new run directory (`<runtime>/<runId>/runtime` for a server, `<runtime>/vt-prep-<runId>-<actor>` in a campaign).

A hard link cannot cross a Windows volume, a Linux mount or a Mac volume, so a linked profile's runtime must be on the install's. On Windows that is the install's NTFS volume. On Linux and macOS it is the install's mount: the same device and the same mount point, because a bind mount of the same disk, or another btrfs subvolume, still refuses the link. A this-machine runtime follows the install: it stays in ValheimTesting's data folder when that folder is on the install's volume or mount, otherwise it is `<install volume>\ValheimTesting\runs` on Windows and `<install mount>/ValheimTesting/runs` on Linux and macOS. It never goes inside the install. Journals and locks stay in the data folder. A macOS client's profile links nothing, so its runtime stays in the data folder.

| Runtime | Result |
|---|---|
| On the install's NTFS volume (Windows) or mount (Linux, macOS) | Staged |
| On another volume or mount, a bind mount, exFAT, FAT32 or a Windows network share | Refused, naming the required location |
| A Linux or Mac folder this user cannot create or write | Refused, naming it |
| An install this user cannot hard-link (Linux's `fs.protected_hardlinks`, on by default, allows a link only to a file the user owns or can write: an install made with `sudo steamcmd` is root's) | Refused: run as the install's owner, or give this user the install |
| An install that is its mount's root (a Docker volume, a disk of its own) | Refused: no folder on that mount lies outside the install. Move the install into a folder of that filesystem and set the inventory `runtime` beside it |
| The install itself, inside it, or holding it | Refused |

On Linux and macOS the preflight's storage probe tries one hard link of the install's server executable into a new folder where the runtime will be, and removes both at once: no listing shows whether the kernel allows the link. `valheim-test env preflight`, `valheim-test session check SESSION --hosts` and the server's own preflight give the same refusals before the profile is staged. A profile is never copied in place of the links. A Steam library on exFAT, FAT32 or a network share cannot hold a profile: move it to NTFS on Windows, or to a filesystem with hard links (ext4, xfs or btrfs on Linux, APFS or Mac OS Extended on macOS).

## Source checks

| When | Check |
|---|---|
| Before staging | Every supplied pin against the source install; a listing of the install: each file's SHA-256, size and modified time, and every folder. The listing is saved once beside the profile as `source-baseline.json` and journalled |
| Before each launch | The profile's bound pins, and every root file a linked profile linked |
| After the run | The source install against the listing, by size and modified time |

Once every process launched from the profile is proven stopped, the run settles the install. It lists the install and compares it with the listing from preparation:

- A changed or missing file fails the run. Each path is named, and the source is never repaired.
- On a linked profile the game runs from the profile and cannot add a file to the install. A path that appears there anyway came from something else, such as Steam, a mod manager or a person. It is named in a warning and left in place, and fails nothing.
- On an install the game runs in place (a macOS client), each file the game added is moved out of the install, for example a preloader log beside the executable or a mod's write to the game root. Each folder the game made is removed once it is empty, deepest first. The install is listed again, and it must equal the listing from preparation. The settlement never follows a link, never moves a path outside the install, and never moves a file of the listing. When a file also changed or went missing, nothing is moved, and every path is named.

| Verdict (`sourceIntegrity.<actor>`) | Meaning | Cleanup step |
|---|---|---|
| `unchanged` | The install lists as before | Passes |
| `restored with a warning` | The game, run in place, added files or folders. They were moved out, and the install lists as before. The warning names every path | Passes |
| `unchanged by the run, with a warning` | Paths appeared in a linked profile's install during the run. The warning names each one; they are left in place | Passes |
| `failed` | A file changed or went missing, an added file could not be moved, or the settlement did not finish. Each path is named, and the source is not repaired | Fails |
| `unknown` | A process launched from the profile was not proven stopped, so nothing was checked | Fails |
| `not applicable` | The client runs its install [in place](#run-an-install-in-place) | Passes |

The moved files go to `source-changes/<actor>/added/` in the run's evidence folder (a session's output, a standalone run's or an arm's), whichever process ran from the profile. Beside them, `source-changes.json` names each added file (its size, SHA-256 and where it is kept), each changed file (before and after), each missing file (`missing`) and folder (`missingDirectories`), and each path left in place with its reason. Moved files stay beside the profile on its host, journalled as kept, when they are past the failure size limits (1 GB per file, 2 GB in all) or the run has no evidence folder (a regression profile removed before any arm ran); `env teardown` removes them.

The listing goes last, after the settlement is journalled. A settlement that does not finish (a host that fails, an abandoned cleanup, a journal that refuses its line) keeps the profile, its listing and the moved files, journalled as kept, and fails the cleanup step. An interrupted or unfinished run's listing stays journalled: `env recover` settles the install from it before it removes the profile ([recovery](sessions-leases-and-recovery.md#env-recover)).

Set `VALHEIM_TESTING_SOURCE_HASH_AFTER_RUN=1` to compare content hashes after the run. A linked profile's game files are hard links: the same file as the install's. Only content hashes find a write through one that keeps the file's size and modified time. On macOS, preparing a client profile also checks the app bundle's code seal ([Mac session limits](platforms.md#mac-session-limits)).

Pins you leave out are observed from the staged profile and recorded as `observed` in `PinOrigins` ([pins](game-actors-and-pins.md#expected-and-observed-pins)).

## Lifecycle

1. The run journals the profile before it stages it.
2. The run stages it and checks it.
3. Once every process launched from it is proven stopped, the run keeps what changed in `runtime-changes/` and removes the profile.

A profile whose process may still run stays, and so does its host lock. `valheim-test env recover --run ID` removes it once the process is gone ([recovery](sessions-leases-and-recovery.md)). Set `VALHEIM_TESTING_KEEP_RUNTIME=1` to keep every profile; `env status` then lists the run as `KEPT`.

On Linux and macOS a launch starts in the profile folder, so Steam's relative `logs/` output lands there and reaches the evidence.

## A clean patchers folder

A clean test runtime is BepInEx core plus your plugins, with an empty `patchers` folder. A preloader patcher left behind by a removed mod breaks the whole run with a `TypeLoadException` on a game type. The `patchers` pin hashes the whole folder, so a leftover patcher is refused before launch. Plans do not name patchers.

## Run an install in place

`--in-place` before the runner's mode, or `"inPlace": true` in a client plan, runs an owned client's install as it is. Nothing is staged and nothing is written into it.

- The install must already hold BepInEx and ValheimCLI; preflight names what is missing.
- Without a `cliManifest`, `cliPreflight` reads `live only: static check not run: your install, run in place`.
- Journals, leases and local test characters work as for a profile.
- Its source verdict is `not applicable`: there is no listing apart from the install it runs.

`--in-place` is for an owned client outside a prepared campaign. It does not apply to a server or a campaign actor. `ClientSession.Launch` refuses an owned plan that is neither bound to a profile nor in place.

## Limits

- A macOS client runs the install's own executable, so a preloader log, or a mod's write to the game root, lands in the install. The source check moves it into the evidence and names it.
- The public launch primitives (`GameLaunch.ForServer`, `GameLaunch.ForClient`, `OwnedServerSession`, `HostServer.StartAsync`, `InteractiveClient.StartAsync`) launch the caller's own directory. They have no profile apart from an install, so no source check runs.
- A profile never takes the install's top-level `logs/` folder.
