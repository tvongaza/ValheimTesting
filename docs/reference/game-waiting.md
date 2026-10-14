# Waiting and owned processes

How a test waits on the game, what an owned launch waits on at startup, how `OwnedServerSession` owns one dedicated server, and how an owned process stops. The types are in [`Valheim.Testing.Game`](../packages/Valheim.Testing.Game.md); hosts on other machines are in [`Valheim.Testing.GameSessions`](../packages/Valheim.Testing.GameSessions.md).

## Waiting

Wait for the event that announces a change, never for time to pass. Every wait takes an explicit, finite timeout; there is no default.

| Outcome | Exception | Reports |
|---|---|---|
| The timeout expires | `WaitTimeoutException` (a `TimeoutException`) | what was awaited, the elapsed time, the last thing seen |
| The wait fails early | `WaitFailedException` (an `InvalidOperationException`) | the same, plus `Context` for a log failure |

| Source | Wait | Notes |
|---|---|---|
| A log line | `LogWait` | Opens at the file's current end or a given byte offset; the file may not exist yet |
| A process exit | Raced for you | Owned sessions race every startup stage against the process exit |
| A game state | `StateWait` | Subscribes to ValheimCLI's state pushes on a connection used for nothing else |
| No event | `ObservedWait.Until` / `UntilAsync` | The one polling fallback: re-observes a read-only source until it matches |

`LogWait` matches complete lines only, checks the optional failure patterns first, and re-reads a truncated or replaced file from its start. A `FileSystemWatcher` wakes it; a re-read every `SafetyInterval` (2 s) covers filesystems that drop watcher events. Open it before launching:

```csharp
using var log = new LogWait(Path.Combine(runtime, "BepInEx", "LogOutput.log")); // before launching
// ... start the server ...
await log.WaitAsync(StartupEvents.CliListening, TimeSpan.FromMinutes(5), [LogWait.Literal("[Fatal")]);
```

For a process you start yourself, use `Process.WaitForExitAsync` with a token from `new CancellationTokenSource(timeout)`.

`StateWait.Connect(host, port)` asks the current state once, then awaits `STATE_CHANGED` pushes with a pending read; nothing is sent while it waits, and a push that arrives during the question counts. A dedicated server's loaded world is `InWorldNoPlayer`; `StateWait.WorldLoaded` lists both loaded states. A state is not mod readiness. The transport keeps no history, so a wait sees pushes from its own subscription on. A failure state, a closed connection or a line that is not a push fails the wait at once. When a wait expires or is cancelled, `StateWait` closes its connection, because the abandoned read leaves the stream's position unknown; later waits on that instance fail, so connect a new one.

`ObservedWait` takes an optional `fails` that ends the wait at once with a reason, a `describe` that names the last observation on expiry, and a `changed` event (such as a `LogWait` for the line the change writes) that ends each pause early. The token cancels the pause too.

`GameActor.WaitForEnvironment` takes an event too, for example `(left, token) => log.WaitAsync(loadLine, left, cancellation: token)`: it rechecks the pins when the line appears rather than every 200 ms. Pins are on [Actors and pins](game-actors-and-pins.md).

## Startup events

`StartupEvents` tells an owned launch what to wait on. `ServerRunPlan.DedicatedStartupEvents(runtime)` builds the dedicated server's; owned client launches use the same failure list.

| Field | Does |
|---|---|
| `CliLog` | This boot's BepInEx log; lines from an earlier boot never count |
| `Failures` | Patterns that end startup at once (`StartupEvents.StartupFailures`) |
| `States` | Opens the `StateWait` for the world-loaded push, for example `() => StateWait.Connect(host, port)` |
| `ReadyStates` | The states that count as loaded; the dedicated plan sets `[StateWait.InWorldNoPlayer]` |

With `Events` set, `OwnedServerSession` opens no connection before this boot's `Command server listening` line (`StartupEvents.CliListening`), then waits for the world-loaded push, then runs the session probe. The process exit is always watched: a server that exits during startup fails it at once with its exit code and the last log line. When that boot wrote nothing to its BepInEx log, the failure says so and points at Unity's player log (`Player.log` or the `-logFile` file) and at security software. Without `Events`, connecting keeps its bounded retries.

`StartupFailures` joins two groups:

| Group | Ends startup on |
|---|---|
| `BepInExPluginLoadFailures` | BepInEx's own "Could not load [" and "Error loading [" lines |
| `RuntimeLoadFailures` | A `TypeLoadException`, `MissingMethodException` or `MissingFieldException` in a warning, error or fatal line, or an exception line starting with its name; an older split ValheimCLI pack logging `CLI core 1.1 is not ready.` |

An info or debug line that only mentions one of these exceptions, such as a handled soft dependency, does not end startup. A log failure carries the matched line and the five lines read before it in `WaitFailedException.Context` and in its message. The same regexes fail the [log scan at teardown](game-log-scan.md).

The startup deadline bounds every step, including a connection or command that blocks: each races the process exit and the time left, a connection that completes after startup gave up is closed, and the final pin verification gets only the time left (at most the command timeout). Only the adapter's own readiness, which has no event, is re-probed at the poll interval.

World entry has one special case: a mod may hold Unity's main thread while ValheimCLI's socket still answers. If ValheimCLI reports during a bounded world-entry wait that a read-only command timed out before it started, `GameActor` reads `STATUS` on a separate connection; a stale `mainThreadIdleMs` means the game is busy, so it waits within the original deadline and sends the observation again once the heartbeat resumes. No mutation is retried, a missing `STATUS` reply fails at once, and a fresh heartbeat paired with an unstarted timeout is reported as a harness fault. `ClientRounds` records the longest frame age as `clientMainThreadStallMs` and the last busy note as `clientBusyNote` in the report's provenance.

## Owned server session

| Type | Does |
|---|---|
| `OwnedServerSession` | Owns one dedicated-server process for a test: `Start()`, `StartAsync()`, `Restart()`, `Stop()`, `Dispose()` |
| `DirectServerProcess` | Starts the server on this machine from a `ProcessStartInfo` ([launch](game-launch.md)) |
| `HostServer` | Starts it on a game host ([GameSessions](../packages/Valheim.Testing.GameSessions.md)) |
| `RecordingTransport` | Records every command the session sends to a file |

Each mod provides its own namespaced session capability, for example `other.mod.tests/session`, through its [test adapter](../packages/Valheim.Testing.Adapter.md#register-the-adapter); the package has no dependency on any mod.

The launch callback receives a fresh, unpredictable token for every boot. Pass it to your child process and have your adapter report the exact token, PID, actual save root, `dedicated` and `complete`, under source `owned-test-session`. Define `complete` to include your mod's readiness. The session requires a valid extension result envelope from the configured owner, verifies process and save identity, then verifies strict pins before it returns an actor. That identity probe is a narrow read-only exception while world loading is incomplete.

Rules of an owned session:

- Startup waits on events and re-probes only incomplete readiness observations, never mutations.
- `Stop()` and `Restart()` own only that process and refuse to start a replacement after a failed cleanup.
- Per-boot logs preserve prior startup evidence; `StartedProcesses` lists every PID started.
- It owns no accounts, machine reservations or external processes.

## Stopping owned processes

A killed game skips its own shutdown: nothing after the last confirmed save is kept, a crossplay lobby stays active, and what mods do at quit never runs. So every owned process is first asked to quit and killed only if it does not exit in time.

`IOwnedProcess.StopCleanly(quit, kill)` returns a `ProcessStop`: its `Outcome` is `Clean`, `Killed` or `AlreadyExited`, with the exit code when known, the time taken and how it was asked. `IOwnedProcess.Stop(timeout)` kills at once, for callers that want that.

| Process | Asked with | Waits (default) |
|---|---|---|
| Owned dedicated server, local (`DirectServerProcess`) | Linux and macOS: SIGINT. Windows: Ctrl+Break when the runner was launched over SSH, Ctrl+C otherwise | plan `quitSeconds` (120) |
| Owned dedicated server on a Linux host (`HostServerProcess`) | SIGINT, only while its start time still matches | plan `quitSeconds` (120) |
| Owned dedicated server on a Windows host (`HostServerProcess`) | Ctrl+Break to that process's console, only while its start time still matches | plan `quitSeconds` (120) |
| Owned client (`ClientSession`) | Windows: its main window is closed. macOS: SIGTERM | `ClientSession.QuitTimeout` (60 s) |
| Client in a host's desktop session (`InteractiveClientProcess`) | Windows: its main window is closed, or killed from an SSH session. macOS: SIGINT | as the caller passes |

The game then saves (a server its world, a client its profile), retires a crossplay lobby and exits if its shutdown handler runs. `quitSeconds: 0` kills at once; the plan refuses a value above 1800. A short `quitSeconds` belongs only to a disposable load smoke that claims no save or lobby retirement; `valheim-test server-load` uses 20 s. A save, restart or crossplay run keeps the longer deadline and verifies the game's shutdown evidence.

`OwnedServerSession.Stops` (and `ServerActor.Stops`) records every stop, restarts included. `PinnedServerRun` writes them to the report as `serverStops` and prints a warning for each killed boot:

```text
boot-1 clean after 9.4 s (Ctrl+C, exit 0); boot-2 killed after 120.0 s (Ctrl+Break; no exit within 120.0 s)
```

`ClientRounds` records the owned client's stop as `clientStop`; `ClientSession.Stopped` holds it. An ordinary run still passes after a kill, because the kill frees the process; a crossplay run does not ([crossplay](game-joins.md#crossplay-playfab)). After a clean stop the [log scan](game-log-scan.md) also sees what the game logged while shutting down.
