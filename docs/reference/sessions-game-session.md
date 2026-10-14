# Game sessions

A `GameSession` is one run's game processes in their roles: a dedicated server or a hosting client, and named clients. Your scenario receives it and drives the actors through named, bounded steps.

The types are in `Valheim.Testing.GameSessions` (`using Valheim.Testing.GameSessions;` beside `using Valheim.Testing.Game;`). The runner builds the session for you: [`PinnedServerRun`](sessions-pinned-server-run.md) for a dedicated server, its hosted modes for a hosting client, and `PinnedServerRun.RunCampaignAsync` for a [campaign](sessions-campaigns.md).

## A scenario

A scenario is `(GameSession session, TPlan plan) => Task`. This one comes from the [FullLifecycle example](../../examples/FullLifecycle/ExampleMod.Tests/MarkerScenario.cs):

```csharp
public static void Run(GameSession session, MarkerPlan plan) =>
    Run(plan, session.Server!.Game, session.Server!, () => session.OpenClient(plan.Client!), session.Report, session.Output, session.Cancellation);
```

The runner starts the session before it calls the scenario and disposes it afterwards. Disposing closes the clients in reverse order, then stops the server, each as a Cleanup step.

## GameSession

| Member | Does | Fails when |
|---|---|---|
| `Server` | The `ServerActor`, or null when a client hosts the world | |
| `Host` | The `HostingClientActor`, or null when there is a dedicated server | |
| `Clients` | The `ClientActor`s by name | |
| `StartAsync()` | Boots the server and brings every client to its menu at once | One actor fails: the others are cancelled, torn down, and the error is rethrown |
| `ServerJoinable()` | Waits until the server accepts joins | The bound expires; the step names itself |
| `Join(client, arrival, protect, lobby)` | Joins one client, optionally at an arrival point, and protects its player | The join is refused or times out |
| `JoinAll(lobby)` | Joins every client | As `Join` |
| `Rejoin(client, …)` | Joins a client again after it left | As `Join` |
| `Barrier(name, wait, within)` | Your own readiness step, bounded by `within` | The bound expires |
| `OpenClient(plan, name, directory)` | Opens one client through a new `ClientActor` and returns its `ClientSession` | |
| `OpenClientsAsync(clients)` | Opens several named clients concurrently, each to its menu | One start fails: the others are closed |
| `Logs`, `AddLog(log)` | The logs the teardown [log scan](game-log-scan.md) reads | |
| `Report`, `Output`, `Cancellation` | The run's `ScenarioReport`, output folder and cancellation token | |

A session has a dedicated server or a hosting client, never both. Each barrier is a Setup step.

## ServerActor

`ServerActor` is the one place a run wires an owned dedicated server from its plan. Each boot gets the plan's environment with a fresh session token, strict pins, test access and its own evidence in the output:

| Evidence | Holds |
|---|---|
| `boot-N/` | The boot's kept logs |
| `boot-N.process.json` | The process record |
| `connection-N.jsonl` | Every command sent to that boot |

| Member | Does |
|---|---|
| `Game` | The current boot's `GameActor` |
| `Start()`, `Restart()` | Starts the server, or stops it and starts it again on the same world |
| `WaitUntilJoinable(server)` | Waits until the server accepts joins |
| `Lobby(server)` | Reads a crossplay server's lobby ([crossplay](game-joins.md#crossplay-playfab)) |
| `Host`, `RuntimeDirectory`, `WorldDirectory` | Where the server runs |
| `Logs`, `LiveLog`, `Stops`, `StartedProcesses` | Its logs and how each boot stopped |

[Owned server session](game-server-session.md) describes how a boot starts and stops.

## ClientActor

A `ClientActor` is the one open path for a session's client. Its logs, a failed startup's included, go to the teardown scan.

| Member | Does |
|---|---|
| `ClientActor.OnThisMachine(name, plan, output)` | A client on this machine, launched or attached as the plan's `mode` says |
| `Start()` | Opens it once and returns its `ClientSession` |
| `Game`, `Session`, `Plan`, `Name` | The opened client |
| `Logs` | The logs to scan |
| `Dispose()` | Closes it |

On this machine an owned client runs from a [disposable profile](sessions-profiles.md). In a campaign, `session.OpenClient(plan.Client, "player")` starts the client on the host its environment was assigned ([campaign clients](sessions-campaigns.md#clients)). The name may be left out when the campaign declares one client. [Clients in system tests](game-client-session.md) describes the plan fields and the client's evidence.

## HostingClientActor

A `HostingClientActor` is a client that hosts its own world: one process that is both the server and a player. `StartAsync` prepares it before any actor starts, then opens it to its menu and hosts the world.

| Member | Does |
|---|---|
| `HostingClientActor.OnThisMachine(name, plan, output)` | A hosting client on this machine, from a plan with a `hostWorld` section |
| `Prepare()` | Runs the plan's preflight, prepares the save directory and places the fixture world |
| `Host()` | Hosts the world with the disposable character, protected |
| `WaitUntilJoinable(host)` | Waits until the host is in its world and its server is open |
| `Restart()` | Leaves the world, which saves it, and hosts it again; peers are disconnected |
| `Game`, `World`, `WorldUid`, `LiveLog`, `Logs` | The host, its placed world and its logs |

The fixture world is placed through the journalled placement ([hosted worlds](game-joins.md#hosted-listen-server-worlds)). When the run so far passed, the host leaves its world at teardown. The world moves into the evidence once no client can still host it; otherwise the report names it in `hostWorldLeftInPlace`.

A peer of the host is a client plan with `"joinsHost": true` and no `join`, `passwordVariable`, `crossplay` or `hostWorld`. `session.Join(name)` waits until the host accepts connections, reads its Steam ID with `cli_multiplayer_identity`, and connects with `cli_connect_steam_user <steamId>`.

## Hosted runs

`PinnedServerRun.MainAsync(args, HostedRunOptions<TPlan>)` runs a hosting client and your scenario on this machine:

```text
<runner> [--in-place] validate-host|host <hosted-plan.json> <new-output-directory>
```

| Mode | Does |
|---|---|
| `validate-host` | Runs the plan's preflight only |
| `host` | Hosts the world, runs the scenario, disposes the session, scans the logs and writes `result.json` and `junit.xml` |

`HostedRunOptions<TPlan>` names `ReadPlan`, `Host` (the plan's hosting client section), `Mod`, `LogScan`, `Scenario`, `CheckPlan` and `Provenance`.

To drive a hosting client's rounds yourself, without a session, call `HostedClientRounds.Run`:

```csharp
new ClientRounds { Client = plan.Client!, Report = report, Output = output }
    .Run(() => ClientSession.Open(plan.Client!, output), measure: round => { /* your checks */ });
```

It places the fixture world through the same journalled placement as a session. A killed run's world is listed by `valheim-test env status` and moved out by `valheim-test env recover --run ID`. [Client rounds](game-client-rounds.md#client-rounds) and [hosted worlds](game-joins.md#hosted-listen-server-worlds) describe the rounds.

## Test fakes

`FakeGameSession.Create` and `FakeGameSession.Hosted(report, output, hostPlan, openClient, peers)` build the same session over scripted clients, with no game. The [FullLifecycle example](../../examples/FullLifecycle/ExampleMod.Tests/MarkerScenarioTests.cs) uses scripted replies the same way.

## Limits

- A hosted world has no password and is never public.
- A crossplay host's peer join is refused.
- The runner's hosted modes place the host and no peers on this machine. A host with peers on other machines is a [campaign without a dedicated server](sessions-campaigns.md#a-campaign-without-a-dedicated-server).
- The Steam peer join is tested with fakes only.
