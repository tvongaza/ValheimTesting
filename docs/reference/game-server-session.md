# Owned server session

How `OwnedServerSession` starts, checks, restarts and stops one dedicated server for a test, and how every owned game process is stopped.

The types are in [`Valheim.Testing.Game`](../packages/Valheim.Testing.Game.md). `PinnedServerRun` and a `GameSession`'s `ServerActor` build the session for you from a plan ([GameSessions](../packages/Valheim.Testing.GameSessions.md#game-sessions)). Build one yourself only when a test drives a dedicated server outside a session.

## Types

| Type | Does |
|---|---|
| `OwnedServerSession` | Owns one dedicated-server process: `Start()`, `StartAsync()`, `Restart()`, `Stop()`, `Dispose()` |
| `DirectServerProcess` | Starts a process on this machine from a `ProcessStartInfo` and keeps its output and logs |
| `HostServer` | Starts the server on a game host ([GameSessions](../packages/Valheim.Testing.GameSessions.md)) |
| `RecordingTransport` | Writes every command and reply to a new JSON Lines file |
| `ProcessStop` | How a stopped process ended: `Outcome`, `ExitCode`, `Elapsed`, `Request` |

## Start a server

The values below come from the [FullLifecycle example](../../examples/FullLifecycle/README.md)'s plan. `runtime`, `world` and `output` are your directories:

```csharp
int boot = 0, connection = 0;
var session = new OwnedServerSession(
    token =>
    {
        var environment = plan.Environment.ToDictionary(e => e.Key, e => plan.Expand(e.Value, runtime, world));
        environment["EXAMPLEMOD_TEST_SESSION_TOKEN"] = token;
        var start = GameLaunch.ForServer(runtime, plan.LaunchArguments(runtime, world), environment).ToStartInfo();
        return new DirectServerProcess(start, Path.Combine(output, "boot-" + ++boot),
            Path.Combine(runtime, "BepInEx", "LogOutput.log"), Path.Combine(runtime, "toolkit-unity.log"));
    },
    () => new RecordingTransport(new CliTransport("127.0.0.1", plan.Port),
        Path.Combine(output, "connection-" + ++connection + ".jsonl")),
    saveRoot: world, expectations: plan.ExpectCommand, sessionCapability: "examplemod.testing/session",
    startup: TimeSpan.FromSeconds(plan.StartupSeconds), command: TimeSpan.FromSeconds(plan.CommandSeconds))
{
    Events = plan.DedicatedStartupEvents(runtime),
    QuitTimeout = TimeSpan.FromSeconds(plan.QuitSeconds),
    EnsureTestAccess = true,
};
GameActor server = session.Start();
```

`Start()` returns a strictly pinned `GameActor` at a loaded world. Pass `-logFile {runtime}/toolkit-unity.log` in the plan's arguments so the kept logs include Unity's own. [`GameLaunch.ForServer`](game-launch.md#dedicated-server-launch) builds the launch.

| Constructor argument | Meaning |
|---|---|
| `launch` | Starts one boot; gets a fresh, unpredictable token for each boot |
| `connect` | Opens a new ValheimCLI connection |
| `saveRoot` | The server's save directory, which the adapter must report |
| `expectations` | The strict pins every boot must hold |
| `sessionCapability` | Your adapter's read-only session command, `owner/command` without arguments |
| `startup`, `command` | The startup deadline and each command's timeout |
| `poll` | How often incomplete readiness is re-read; 500 ms by default |

## What startup checks

1. With `Events` set, it opens no connection before this boot's `Command server listening` line ([startup events](game-waiting.md#startup-events)). A `StartupEvents.StartupFailures` line ends startup at once.
2. With `Events.States` set, it waits for the world-loaded state push.
3. It reads `cli_extension <sessionCapability>` until the adapter reports this boot's exact token and PID, the save root, `dedicated` and `complete`, under source `owned-test-session`. An identity mismatch is refused at once.
4. It verifies the strict pins with the time left, at most the command timeout.
5. With `EnsureTestAccess`, it establishes test access, devcommands then `confirmcheats` ([test access](game-player-placement.md#join-a-dedicated-server)).

Every step races the process exit and the startup deadline. An exit fails startup with the exit code and the last log line. If that boot wrote nothing to its BepInEx log, the failure says so and points at Unity's log and at security software. Without `Events`, connecting retries within the deadline. Only incomplete readiness is re-read; no mutation is retried.

Define `complete` in your adapter to include your mod's readiness. The session needs no dependency on any mod: your [test adapter](../packages/Valheim.Testing.Adapter.md#register-the-adapter) provides the session command.

`WaitUntilJoinable(server)` waits, within the startup deadline, until the adapter reports `acceptingConnections`. A first boot opens its game socket well after the world loads, so call it right before the first join.

## Restart and stop

| Member | Does |
|---|---|
| `Restart()` | `Stop()`, then `Start()`; refuses to start a new boot after a failed stop |
| `Stop()`, `Dispose()` | Asks the server to quit, waits `QuitTimeout` (2 minutes by default), then kills it |
| `StartedProcesses` | The PID of every boot started |
| `Stops` | One `ProcessStop` per stop, restarts included |

The session owns only the processes it started. It owns no accounts, machine reservations or other processes.

`DirectServerProcess(start, logPrefix, gameLogs)` writes `<logPrefix>.stdout.log` and `<logPrefix>.stderr.log`. When it stops, it copies each game log to `<logPrefix>.game-N.log`, or writes `<logPrefix>.game-N.log.absent` when the game made none. A server a session starts on a host keeps its logs in that boot's directory; a Windows server there starts from a short-lived scheduled task and keeps no stdout.

## Stopping owned processes

A killed game skips its own shutdown: nothing after the last confirmed save is kept, a crossplay lobby stays open, and what mods do at quit never runs. So every owned process is first asked to quit, and killed only if it does not exit in time.

`IOwnedProcess.StopCleanly(quit, kill)` returns a `ProcessStop`. Its `Outcome` is `Clean`, `Killed` or `AlreadyExited`. `IOwnedProcess.Stop(timeout)` kills at once.

| Process | Asked with | Waits |
|---|---|---|
| Server on this machine (`DirectServerProcess`) | Linux and macOS: SIGINT. Windows: Ctrl+C, or Ctrl+Break when the runner runs over SSH | `quitSeconds` (120) |
| Server on a Linux or macOS host (`HostServerProcess`) | SIGINT, only while its start time still matches | `quitSeconds` (120) |
| Server on a Windows host (`HostServerProcess`) | Ctrl+Break, only while its start time still matches | `quitSeconds` (120) |
| Owned client (`ClientSession`) | Windows: its main window is closed. macOS: SIGTERM | `ClientSession.QuitTimeout` (60 s) |
| Client in a host's desktop session (`InteractiveClientProcess`) | Windows: its main window is closed, through a desktop task from session 0. macOS: SIGINT | as the caller passes |

`quitSeconds` is a `ServerRunPlan` field: 0 kills at once, and the plan refuses more than 1800. Keep a short value for a load smoke that claims no save; `valheim-test server-load` uses 20 s. A save, restart or crossplay run keeps the longer deadline.

`PinnedServerRun` writes the stops to the report as `serverStops` and warns for each killed boot:

```text
boot-1 clean after 9.4 s (Ctrl+C, exit 0); boot-2 killed after 120.0 s (Ctrl+Break; no exit within 120.0 s)
```

`ClientRounds` records the owned client's stop as `clientStop`. An ordinary run still passes after a kill; a [crossplay](game-joins.md#crossplay-playfab) run does not. After a clean stop, the [log scan](game-log-scan.md) also sees what the game logged while it shut down.

## Recorded commands

`RecordingTransport(inner, file)` creates `file` and refuses one that exists. Each line holds the UTC time, the command and the reply or error. Steam IDs and PlayFab IDs in `cli_multiplayer_identity` and `cli_connection_status` replies are redacted. The file can still hold world names, positions and other IDs: review it before you publish it.

## Limits

- The session command is a read-only exception while the world loads. Your adapter must keep it read-only.
- A stop proves only how the process ended. Whether the world saved is the game's shutdown evidence, which the [log scan](game-log-scan.md) reads.
- A Windows server started on a host keeps no stdout log.
