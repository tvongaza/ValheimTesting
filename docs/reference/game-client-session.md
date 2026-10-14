# Game clients in system tests

How a test describes one game client with a `ClientRunPlan`, opens it with `ClientSession`, and what the session checks and writes.

The types are in [`Valheim.Testing.Game`](../packages/Valheim.Testing.Game.md). A client is `owned` (the run launches and stops it) or `attach` (you launched it; the run only connects). A `GameSession` opens its clients for you ([game sessions](sessions-game-session.md)). The [FullLifecycle example](../../examples/FullLifecycle/README.md) uses both modes.

## Client plan fields

A minimal owned client; the path, port, character and GUID are illustrative:

```json
"client": {
  "mode": "owned",
  "install": "C:\\Program Files (x86)\\Steam\\steamapps\\common\\Valheim",
  "port": 5689,
  "join": "127.0.0.1:2486",
  "character": "TestDummy",
  "pins": { "example.mymod": "absent" }
}
```

JSON names are the camelCase of `ClientRunPlan`'s properties.

| Field | Meaning | Default or rule |
|---|---|---|
| `mode` | `owned` or `attach` | Required |
| `install` | Owned: the client install; the run launches a disposable profile of it | Full path on the client's machine |
| `inPlace` | Owned: run `install` as it is; nothing is staged into it | `false`; `--in-place` sets it for every owned client of a run |
| `launchArguments` | Owned: extra game arguments | `[]`; `-console` is added; `-logFile` and `--doorstop-*` refused |
| `environment` | Owned: non-secret process variables | `{}`; Doorstop variables, invalid names and multiline values refused |
| `architecture` | Owned: the slice a `Valheim.app` client runs as, `x64` or `arm64` | `arm64` on a Mac unless it is an Intel Mac; `x64` elsewhere |
| `host` | The client's ValheimCLI host | `127.0.0.1`; an owned or hosting client is on this machine |
| `port` | The client's ValheimCLI port | 1024 to 65535; differs from the server's |
| `pins` | Every plugin the client loads, exact MD5 or `absent` | No world key ([pins](game-actors-and-pins.md#expected-and-observed-pins)) |
| `join` | The server address, `host:port` | Leave out for `crossplay`, `hostWorld` or `joinsHost` |
| `crossplay` | Join the server's PlayFab lobby | `false` ([crossplay](game-joins.md#crossplay-playfab)) |
| `hostWorld` | The client hosts this fixture world from its menu | Omitted ([hosted worlds](game-joins.md#hosted-listen-server-worlds)) |
| `joinsHost` | Join the run's hosting client | `false` |
| `character` | An existing, disposable local character | Required; never a cloud character |
| `targetable` | Protect the player without ghost mode, so creatures notice it | `false` ([protection](game-player-placement.md#protection-changes-gameplay)) |
| `passwordVariable` | The variable that holds the join password | Omitted |
| `startSeconds` | Bounds the launch to the main menu | 300; 10 to 1800 |
| `bepInExSeconds` | Owned: time for BepInEx's first log line | 60; 5 to 1800, capped at `startSeconds` |
| `joinSeconds` | Bounds a join or a hosted start | 180; 10 to 900 |
| `arrivalSeconds` | Bounds an arrival | 120; 10 to 600 |
| `installPins` | Owned: game build, loader and patchers by SHA256 (`InstallPins.Of`) | Required in place; a profile observes and enforces the staged values |
| `capabilities` | ValheimCLI commands the run uses, `owner/command` | `[]`; checked before launch and live |
| `cliManifest` | Owned: full path of one ValheimCLI build's manifest | A profile stages and checks the pinned bundle; in place, no static check ([manifest](game-preflight.md#valheimcli-capability-manifest)) |

The profile itself, what it links and what it leaves out, is on [disposable profiles](sessions-profiles.md).

## Plan checks

`Validate(params absentPlugins)` refuses a plan before anything starts. Each `absentPlugins` entry must be pinned `absent`: the claim is what a client without it sees.

| Refused | Rule |
|---|---|
| `mode` other than `owned` or `attach` | `Client mode is owned or attach.` |
| An owned client without a full install path, or with a host other than `127.0.0.1` or `localhost` | Owned clients run on this machine |
| An attached client with `install`, `installPins`, `launchArguments`, `architecture`, `inPlace`, `environment` or `cliManifest` | Those belong to its operator |
| `arm64` for a Windows client | A Windows client is x64 only |
| A `Valheim.app` install on this machine that cannot launch as the plan's slice | The [launch's slice check](game-launch.md#the-macos-slice-and-core-check) runs here |
| `hostWorld` with `join`, `passwordVariable` or `crossplay`, or not on this machine | A hosting client joins no server |
| `joinsHost` with `join`, `passwordVariable`, `crossplay` or `hostWorld` | A host's peer joins by identity |
| `crossplay` with `join` or `passwordVariable` | The crossplay join would carry the password as text |
| `join`, `character` or `passwordVariable` with whitespace | Single tokens |
| A timeout out of range, or a capability listed twice or not `owner/command` | See the field table |
| `pins` with `worlduid` or `world` | The runner pins the server's world |
| A ValheimCLI pin that is not an exact MD5, unless a profile stages it | `Pin the client's exact ValheimCLI MD5.` |
| An in-place client without `installPins` | `InstallPins.Of` computes them |

`MenuExpectations` (plugins only) and `WorldExpectations(worldUid)` (plugins and the server's world) turn the pins into strict `cli_expect` commands.

`RequirePasswordSource()` refuses an owned client whose password variable is in `environment` (serialized plan data) or missing from the runner's process.

`Preflight()` runs the [static install checks](game-preflight.md) before anything is staged or started.

## Open a client

| Member | Does |
|---|---|
| `ClientSession.Launch(plan, output)` | Starts an owned client and returns it at its main menu, strictly pinned |
| `ClientSession.Attach(plan, output)` | Connects to an operator's client and verifies its menu pins |
| `ClientSession.Open(plan, output, logs)` | Launches or attaches by `mode`, and adds an owned client's kept logs to `logs`, also when startup fails |
| `ClientSession.Launch(plan, output, start, connect, ready)` | Takes the process, connection and readiness as functions, for a no-game test ([test fakes](../packages/Valheim.Testing.Game.md#test-fakes)) |

`Launch` runs only an install bound to a disposable profile, or one in place; your install is never launched unless you ask. Before it starts anything it refuses, in this order:

1. an install whose game build, loader or patchers differ from `installPins`;
2. a failed [preflight](game-preflight.md) install check, or a launch [`GameLaunch.ForClient`](game-launch.md#game-client-launch) refuses;
3. a process with no interactive desktop session (Windows session 0);
4. something already listening on the client's CLI port;
5. no Steam client running on this machine;
6. the password variable missing from its environment.

### Startup

Startup waits on events, never on polling, each raced against the process exit and `startSeconds` ([startup events](game-waiting.md#startup-events)):

1. this launch's first BepInEx log line within `bepInExSeconds`; without it the game runs without BepInEx, and startup fails naming the loader;
2. ValheimCLI's listening line in this launch's BepInEx log; a `StartupEvents.StartupFailures` line or `[Steamworks.NET] SteamAPI_Init() failed` ends it;
3. the `MainMenu` state push.

An exit ends startup at once with the exit code, naming Unity's log when BepInEx wrote nothing, or Steam's sign-in elsewhere when Steam logged it. Once the client answers, the session verifies the menu pins and requires `capabilities` live (`CliCapabilities.Require`), for an attached client too. A failed startup stops the process it started.

### Unity's log and the user's files

The owned launch passes `-logFile` with `toolkit-unity.log` in the run's launch folder (`output`), so the game never writes the user's own `Player.log`.

The game also writes its recent-servers list (`serverlist_local/recent` and `recent.old`) into the user's data folder. Before the start, the session backs those files up into the evidence, journals the backup and moves them aside. Once the client is proven stopped, they go back, and the run's versions stay in the backup's `after` folder. After an unproven stop, `valheim-test env recover` restores them.

## Evidence a client session writes

| File or member | Holds |
|---|---|
| `client-commands.jsonl` (then `-2`, `-3`) | Every command the session sent; evidence is never overwritten |
| `client-process.json` | The owned process's PID, start time, install and `architecture` |
| `client-boot.stdout.log`, `.stderr.log`, `.game-0.log`, `.game-1.log` | The process output, then its BepInEx log and `toolkit-unity.log`, copied at the stop |
| `client-boot.game-2.preloader-N.log` | A BepInEx preloader crash log this launch wrote, when BepInEx never logged |
| `client-failure-diagnostic.json` | What a failed client showed before it was stopped ([debug a native run](../guides/debug-a-native-run.md)) |
| `user-files-<run>-<label>` | The backup of the user's recent-servers list |
| `Logs` | The kept BepInEx and Unity logs, for the [log scan](game-log-scan.md) |
| `Architecture`, `ProcessId`, `Stopped`, `Closed` | The launched slice, the process, how it stopped, and whether the session is closed |

## Limits

- An owned client must run in the desktop session where Steam is running and signed in. `ClientSession` can tell only that Steam runs, not that it is signed in, and never signs in.
- Disposing asks an owned client to quit, so the game saves its profile, and kills it after `QuitTimeout` (one minute) ([stopping owned processes](game-server-session.md#stopping-owned-processes)).
- In Valheim 1.0 a joined client's cheat commands are refused whatever the server's admin list says. ValheimCLI's client setting `AllowOnServerClients = true` lets its test commands run there.
- An attached client's install is its operator's and is never read, so its game build is not pinned. Use an owned client when the build matters.
