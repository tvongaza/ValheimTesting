# Game clients in system tests

A system test describes one game client with a `ClientRunPlan` section and opens it with `ClientSession`. The plan says whether the run launches the client (`owned`) or connects to one you launched (`attach`), what it joins, and what it pins. The [FullLifecycle example](../../examples/FullLifecycle/README.md) uses both.

## Client plan fields

JSON names are the camelCase of `ClientRunPlan`'s properties.

| Field | Meaning | Default or rule |
|---|---|---|
| `mode` | `owned`: the run launches and stops the client. `attach`: you launch it; the run only connects | Required |
| `install` | Owned only: the client install. The run launches a disposable copy of it, never the install itself | Full path on the client's machine |
| `inPlace` | Owned only: run `install` as it is; nothing is staged into it | `false`; `--in-place` sets it for every owned client of a run |
| `launchArguments` | Owned only: extra game arguments | `[]`; `-console` is added by the launch |
| `environment` | Owned only: non-secret process variables for the client, such as a mod's documented switch | `{}`; loader overrides, invalid names and multiline values refused; attached plans refuse it |
| `architecture` | Owned only: the slice a `Valheim.app` client runs as, `x64` or `arm64` | Host default: `arm64` on Apple Silicon, `x64` elsewhere ([native client](platforms.md#native-apple-silicon-client)) |
| `host` | The client's ValheimCLI host | `127.0.0.1`; an owned or hosting client must be on this machine |
| `port` | The client's ValheimCLI port (`[Server] Port`) | 1024 to 65535; differs from the server's |
| `pins` | Every plugin the client loads, exact MD5 or `absent` | No world key ([expected and observed pins](game-actors-and-pins.md#expected-and-observed-pins)) |
| `join` | The server address, `host:port` | Leave out for `crossplay`, `hostWorld` or `joinsHost` |
| `crossplay` | Join the server's PlayFab lobby instead of an address | `false` ([crossplay](game-joins.md#crossplay-playfab)) |
| `hostWorld` | The client hosts this fixture world from its menu | Omitted ([hosted worlds](game-joins.md#hosted-listen-server-worlds)) |
| `joinsHost` | The client joins the run's hosting client by its multiplayer identity | `false` |
| `character` | An existing, disposable local character | Required; never a cloud character |
| `targetable` | Protect the player without ghost mode, so creatures notice it | `false` ([protection](game-player-placement.md#protection-changes-gameplay)) |
| `passwordVariable` | The variable in the client's process that holds the join password | Omitted |
| `startSeconds` | Bounds the launch to the main menu | 300; 10 to 1800 |
| `bepInExSeconds` | Owned only: how long BepInEx may take to write this launch's first log line | `60`; 5 to 1800, never more than `startSeconds` |
| `joinSeconds` | Bounds a join or a hosted start | 180; 10 to 900 |
| `arrivalSeconds` | Bounds an arrival | `120`; 10 to 600 |
| `installPins` | Owned only: game build, loader and patchers by SHA256 (`InstallPins.Of`) | Required in place; a disposable copy may omit them and observes the staged values |
| `capabilities` | ValheimCLI commands the run uses, `owner/command` | `[]`; checked against `cliManifest` before launch and live once the client answers |
| `cliManifest` | Owned only: full path of the manifest of one ValheimCLI build | Omitted: a copy checks against the pinned bundle's; in place, no static check ([manifest](game-preflight.md#valheimcli-capability-manifest)) |

## Plan checks

`Validate(params absentPlugins)` refuses a plan before anything starts. Each `absentPlugins` entry must be pinned `absent`: the claim is what a client without it sees.

| Refused | Rule |
|---|---|
| `mode` other than `owned` or `attach` | `Client mode is owned or attach.` |
| An owned client without a full install path, or with a host other than `127.0.0.1` or `localhost` | Owned clients run on this machine |
| An attached client with `install`, `installPins`, `launchArguments`, `architecture`, `inPlace`, `environment` or `cliManifest` | Those belong to its operator |
| `architecture` other than `x64` or `arm64`; `arm64` for a Windows client | A Windows client is x64 only |
| A `Valheim.app` install on this machine that cannot launch as the plan's slice | The launch's slice and core check runs here |
| `hostWorld` with `join`, `passwordVariable` or `crossplay`, or with a host that is not this machine | A hosting client joins no server |
| `joinsHost` with `join`, `passwordVariable`, `crossplay` or `hostWorld` | A host's peer joins by identity |
| `crossplay` with `join` or `passwordVariable` | The crossplay join command would carry the password as text |
| `join`, `character` or `passwordVariable` with whitespace | Single tokens |
| A timeout out of range | See the field table |
| A capability listed twice or not `owner/command` | Name each once |
| `pins` with `worlduid` or `world` | The runner pins the server's world |
| A ValheimCLI pin that is not an exact MD5 (any client but a disposable copy) | `Pin the client's exact ValheimCLI MD5.` |
| A pin that is neither an exact MD5 nor `absent` | Per plugin |
| An in-place owned client without `installPins` | `InstallPins.Of` computes them |

Two properties turn the pins into strict `cli_expect` commands: `MenuExpectations` (plugins only) and `WorldExpectations(worldUid)` (plugins and the server's world). `Validate` parses the pins through `MenuExpectations` first.

`RequirePasswordSource()` refuses an owned joined client whose password variable is in `environment` (plan data is serialized) or missing from the runner's process. The launched client inherits the variable.

`Preflight()` runs every static check after `Validate` and before anything is staged or started: the hosted fixture's pinned files and world UID, then an owned install's pins, loader, plugin builds, standing pins and ValheimCLI set. `Preflight(capabilities)` adds the runner's own commands to the manifest check; `CheckCliManifest(capabilities)` and `CheckInstallPins()` run those two parts alone. `CliPreflight` records which checks apply (`static and live`, or a `live only` reason). See [Owned-run preflight](game-preflight.md).

## Open a client

| Member | Does |
|---|---|
| `ClientSession.Launch(plan, output)` | Starts an owned client through [`GameLaunch.ForClient`](game-launch.md#game-client-launch) and returns it at its main menu, strictly pinned |
| `ClientSession.Attach(plan, output)` | Connects to an operator's client and verifies its menu pins; disposing never touches that process |
| `ClientSession.Open(plan, output, logs)` | Launches or attaches by `mode`, and adds an owned client's kept logs to `logs` for the teardown scan, also when startup fails after the process started |
| `GameSession.OpenClient` | The same for a session's `Logs`, so a client that never reached its menu is still scanned and listed in `result.json` ([GameSessions](../packages/Valheim.Testing.GameSessions.md)) |
| `ClientSession.Launch(plan, output, start, connect, ready)` | Takes the process, connection and readiness as functions, so a no-game test can drive startup failures, timeouts and cleanup ([test fakes](../packages/Valheim.Testing.Game.md#test-fakes)) |

`Launch` launches only an install bound to a disposable runtime (a session's, a targeted regression's) or one run in place. Any other owned plan is refused: your install is never launched unless asked. Before it starts anything it refuses, in this order:

1. an install whose game build, loader or patchers differ from `installPins`;
2. a failed install check of the [preflight](game-preflight.md): a pinned plugin build that is not installed, a script ScriptEngine does not load at start, a standing expectations file that would refuse the run, a ValheimCLI set that is not its `cliManifest`'s or lacks a capability;
3. a process with no interactive desktop session (Windows session 0);
4. something already listening on the client's CLI port;
5. no Steam client running in this session;
6. the password variable missing from its own environment.

Startup then waits on events, never on polling, each raced against the process exit ([startup events](game-waiting.md#startup-events)):

1. this launch's first BepInEx log line within `bepInExSeconds`: a game still running without it runs without BepInEx, and startup fails naming the loader;
2. ValheimCLI's listening line in this launch's BepInEx log; any of `StartupEvents.StartupFailures` ends it;
3. the `MainMenu` state push.

An exit ends startup at once with the exit code and, when BepInEx wrote nothing, the path of `Player.log`. Once the client answers at its menu, `capabilities` are required live (`CliCapabilities.Require`), for an attached client too. A failed startup stops the process it started.

`PinnedFile` pins a plan's input file by `Source` and `Sha256`; `Verified()` rechecks the hash before use and refuses with `<Source> changed after it was pinned.`

## Evidence a client session writes

| File or member | Holds |
|---|---|
| `client-commands.jsonl` (then `-2`, `-3`, and so on) | Every command the session sent; evidence is never overwritten |
| `client-process.json` | An owned client's launch, including the `architecture` it launched |
| `Architecture` | The launched slice; null for an attached client |
| `clientArchitecture` | The same in `ClientRounds`' provenance; `attached` for an attached client ([client rounds](game-client-rounds.md#client-rounds)) |
| `Logs` | The kept BepInEx log and `Player.log` of an owned client, for the [log scan](game-log-scan.md) |
| `ProcessId`, `Stopped`, `Closed` | The owned process, how it stopped, and whether the session is closed |

## Limits

- An owned client must run in the desktop session where Steam is running and signed in. `ClientSession` can tell only that Steam runs, not that it is signed in, and never signs in.
- Disposing asks an owned client to quit (it closes the game's main window on Windows, SIGTERM on macOS), so the game saves its profile as a player's quit does, and kills it after `QuitTimeout` (one minute). `Stopped` says which.
- In Valheim 1.0 a joined client's cheat commands are refused whatever the server's admin list says. ValheimCLI's client setting `AllowOnServerClients = true` is what lets its test commands run there.
- An attached client's install is its operator's and is not read, so its game build is not pinned. Use an owned client when the build matters.
