# Waiting

How a test waits on the game, and what an owned launch waits on at startup. Owning and stopping a server is on [Owned server session](game-server-session.md). The types are in [`Valheim.Testing.Game`](../packages/Valheim.Testing.Game.md); hosts on other machines are in [`Valheim.Testing.GameSessions`](../packages/Valheim.Testing.GameSessions.md).

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

[`OwnedServerSession`](game-server-session.md#what-startup-checks) and [`ClientSession`](game-client-session.md#startup) wait on these in order, and race every stage against the process exit.

`StartupFailures` joins two groups:

| Group | Ends startup on |
|---|---|
| `BepInExPluginLoadFailures` | BepInEx's own "Could not load [" and "Error loading [" lines |
| `RuntimeLoadFailures` | A `TypeLoadException`, `MissingMethodException` or `MissingFieldException` in a warning, error or fatal line, or an exception line starting with its name; an older split ValheimCLI pack logging `CLI core 1.1 is not ready.` |

An info or debug line that only mentions one of these exceptions, such as a handled soft dependency, does not end startup. A log failure carries the matched line and the five lines read before it in `WaitFailedException.Context` and in its message. The same regexes fail the [log scan at teardown](game-log-scan.md).

World entry has one special case: a mod may hold Unity's main thread while ValheimCLI's socket still answers. If ValheimCLI reports during a bounded world-entry wait that a read-only command timed out before it started, `GameActor` reads `STATUS` on a separate connection; a stale `mainThreadIdleMs` means the game is busy, so it waits within the original deadline and sends the observation again once the heartbeat resumes. No mutation is retried, a missing `STATUS` reply fails at once, and a fresh heartbeat paired with an unstarted timeout is reported as a harness fault. `ClientRounds` records the longest frame age as `clientMainThreadStallMs` and the last busy note as `clientBusyNote` in the report's provenance.
