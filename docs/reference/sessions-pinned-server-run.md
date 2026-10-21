# Pinned server runner

`PinnedServerRun.MainAsync` is the whole lifecycle of a mod's dedicated-server test runner. Your `Program.cs` supplies the plan type and the scenario; the runner stages, starts, joins, tears down and reports.

It is in `Valheim.Testing.GameSessions`. `MarkerScenario.RunnerOptions` in the [FullLifecycle example](../../examples/FullLifecycle/README.md) holds these options; the example runs them as a session from xUnit.

## Usage

```text
<runner> [--in-place] [--inventory <environments.json>] validate|run <plan.json> <new-output-directory>
```

| Option or mode | Does |
|---|---|
| `validate` | Checks the plan, stages the profile and copies the world; launches nothing |
| `run` | Does the same, then starts the server and runs the scenario |
| `--inventory FILE` | Runs the server on the first fitting server environment of an [inventory file](sessions-inventory.md) |
| `--in-place` | Owned clients run their install as it is ([profiles](sessions-profiles.md#run-an-install-in-place)) |

Without `--inventory`, the server runs on this machine as the `local-server` environment, from the install the plan's `runtime` names.

```csharp
public static Task<int> Main(string[] args) => PinnedServerRun.MainAsync(args, new PinnedServerRunOptions<MarkerPlan>
{
    Name = "examplemod-system-test",
    ReadPlan = path => MarkerPlan.Validated(ServerRunPlan.Read<MarkerPlan>(path)),
    Mod = MarkerPlan.Mod,
    ClientPlans = plan => plan.Client is null ? [] : [plan.Client],
    Scenario = (session, plan) => Task.Run(() => MarkerScenario.Run(session, plan)),
});
```

| Option | Does |
|---|---|
| `Name` | The runner's name in usage and reports |
| `ReadPlan` | Reads and checks your plan type |
| `Mod` | The `ModDeclaration`: session capability, token variable and declared Harmony patches |
| `CheckPlan` | Your plan rules, applied to the plan as it will run |
| `ClientPlans` | The plan's client sections, so the runner checks each `passwordVariable` first |
| `Scenario` | `(GameSession session, TPlan plan) => Task` |
| `Provenance` | Extra provenance entries |
| `CleanupBudget` | How long cleanup may take after Ctrl+C; 5 minutes by default (`RunCancellation.CleanupBudget`) |

When the server's pins load the mod, the runner checks the declared patches at runtime-ready, as the Setup step "server: the mod's Harmony patches are applied".

## The plan

A plan derives from `ServerRunPlan`, which is read strictly: an unknown field is refused. `ValidateServerPlan` applies these rules:

| Rule | Refuses |
|---|---|
| Sources | A relative source, or a supplied manifest that does not match |
| Bounds | A port or time outside its range |
| Executable | An unknown server executable |
| Environment | A runner-owned token or a Doorstop variable |
| Arguments | Missing `-batchmode -nographics`, or not exactly one `-savedir {world}` |
| Pins | A supplied pin that does not match exactly |
| Required plugins | A plugin the runner requires that the staged set lacks |

Pins you leave out are observed from the staged profile and the copied world ([pin origins](game-actors-and-pins.md#pin-origins-in-the-report)). `logScan` sets the run's own [log scan](game-log-scan.md) severities and patterns, each with a reason.

A join password never goes in the plan. Set the variable that `passwordVariable` names, for example `VT_JOIN_PASSWORD`, in the runner's environment. The runner checks it before it writes evidence or stages anything.

## What a run does

1. Refuses an existing output directory, checks the host, and checks the output drive has room for the world copy plus headroom (2 GB, or a tenth of the copy when that is larger). `freeBytesBeforeCopies` records what it saw.
2. Takes the server host's lock, checks every supplied pin against the install, stages the server's [disposable profile](sessions-profiles.md), and binds the omitted pins from it. It then copies the world and checks it.
3. Stops here for `validate`.
4. Checks the CLI port, starts the server from the profile on the world copy, establishes test access on every boot, and runs the scenario.
5. Always disposes the session, scans the logs, writes `result.json` and `junit.xml`, and prints the result.

| Printed | Exit code | Means |
|---|---|---|
| `PASS` | `0` | Every step of `run` passed |
| `VALIDATED` | `0` | Every step of `validate` passed |
| `FAIL` | `1` | A step failed |
| Usage | `2` | Bad arguments |
| `UNKNOWN` | `3` | The only failures are host operations with an unknown outcome |
| `ABANDONED cleanup` | `3` | A second Ctrl+C stopped a cleanup that hung |

Ctrl+C and SIGTERM cancel the run. Cleanup then runs on its own token, within `CleanupBudget`. After `ABANDONED cleanup`, `valheim-test env recover --run ID` finishes it ([recovery](sessions-leases-and-recovery.md)).

## Phases and states

Every step is recorded in one `StepPhase`:

| Phase | Holds |
|---|---|
| `Preflight` | Checks before anything is copied |
| `Setup` | Staging, copying, launching, joining, test access, arrival, and the transitions between rounds |
| `Scenario` | Your steps; the default for `Step(name, action)` |
| `Cleanup` | Stopping, releasing, removing the profile, and the log scan |

`result.json` derives four states from the steps:

| State | True when |
|---|---|
| `PreflightPassed` | Every Preflight step passed |
| `RuntimeReady` | Preflight and every Setup step passed |
| `ScenarioPassed` | The runtime was ready, and at least one Scenario step ran and all passed |
| `CleanupVerified` | At least one Cleanup step ran and all passed |

`junit.xml` has one `testsuite` per phase, named `<report> / <phase>`. A run passes only when every step passes, so a passed scenario with a failed cleanup prints `FAIL`. Tag your own setup steps with `report.Step(StepPhase.Setup, ...)` or `round.Step(StepPhase.Setup, ...)`.

## What ran

| Field in `result.json` | Holds |
|---|---|
| `Toolkit` | The toolkit that wrote the result (`ToolkitProvenance`) |
| `Packages` | Each toolkit and ValheimCLI assembly: version, source commit, state and SHA-256 |
| `LoadedAssemblies` | Every assembly the process loaded, the entry runner included |
| `Plugins` | Per actor, each plugin GUID's MD5 as the game confirmed it, or `absent` |
| `PinOrigins` | Whether each pin came from the plan or was observed |

A `Valheim.Testing*` assembly is `released` when the release workflow built it, `candidate` when its version carries `-candidate`, and `unreleased` otherwise. A source-built `valheim-test` proves only that source tree. To claim a package passed, run a fresh consumer of the exact packages.

## What the output keeps

| Kept | Holds |
|---|---|
| `result.json`, `junit.xml` | Every step and the provenance |
| The plan and `input-hashes.json` | What the run read |
| `boot-N/`, `connection-N.jsonl` | Each boot's logs and commands |
| The world copy | The run's save |
| `runtime-changes/` | Every file the run added or changed in the profile, with `changes.json` |
| `host-runs/<host>/<runtime folder>/` | What was left in the run's folder on each host, such as a client's launch folders and user-file backup, fetched before the folder was removed |

The profile goes once its server has stopped, on a pass, a failure or `validate`. `runtime-changes/` keeps at most 64 MB per file and 256 MB in all after a pass, and 1 GB and 2 GB after a failure. A larger file is listed with its size and SHA-256. `runtimeCopy` says what was removed and kept, and `outputBytes` gives the output's size.

If the server did not stop cleanly, the profile and the host lock stay. The report names them, and `valheim-test env recover` removes them once the server has stopped. Set `VALHEIM_TESTING_KEEP_RUNTIME=1` to keep the whole profile for debugging.

## A server on another host

With `--inventory`, the runner places the server on the first server environment, in inventory order, that fits the plan. It records the choice and every skipped environment as `serverEnvironment` in `result.json`, beside `inventorySha256`. When none fits, the run is refused before anything is written. A Linux or macOS environment's profile storage is not judged at placement, because its verdict needs a trial link: the run's own storage preflight ("check the Linux profile storage") refuses it before the lock or staging, naming the environment and its `runtime`, and that trial link under the runtime root, made and removed at once, is the only write before the refusal ([where a profile lives](sessions-profiles.md#where-a-profile-lives)).

An environment fits when its `cliPort` is the plan's `port` and its `gamePort` matches any `-port` in the plan's arguments. On the host, the runner then:

1. holds the host's lock for the whole run;
2. checks the supplied pins against the host's install, then stages the profile in `<runtime>/<runId>/runtime`;
3. ships the verified world copy to `<runtime>/<runId>/world` and verifies it there;
4. opens a loopback tunnel to ValheimCLI and starts each boot in `<runtime>/<runId>/boot-N` with `HostServer.StartAsync`;
5. stops only the process it started, by process ID and start time, and fetches `boot-N/` and `host-world/`; after each verified fetch, records its receipt in the journal and removes the corresponding host directory under the host lock. A failed fetch or receipt keeps the directory visible to `env status`; recovery keeps evidence without a proven fetch, and explicit `env teardown --run ID` removes it. Last, whatever is left in `<runtime>/<runId>` is fetched into `host-runs/<host>/` in the output and the folder is removed, under the same rules. A client's `<runtime>/<runId>` on its host goes the same way;
6. settles the source install against its listing from preparation ([source checks](sessions-profiles.md#source-checks)): a file the game added is moved into `source-changes/server/` and named, and the run passes with a warning; a changed or missing file fails the run and is never repaired.

Set `VALHEIM_TESTING_SOURCE_HASH_AFTER_RUN=1` to compare content hashes instead of sizes and times. [Game hosts](sessions-hosts.md) describes each host kind, and [run on another machine](../guides/run-on-another-machine.md) walks through it.

| Plan field | On a host |
|---|---|
| `runtime.source` | Not read; the environment's `install` is the runtime |
| `crossplay` | Starts the server with `-crossplay` |

A crossplay server on Linux needs `libpulse0` and `libpulse-mainloop-glib0`, which `libparty.so` loads; step 2 checks them. On Windows the preflight requires `Party.dll`.

## Limits

- A standalone `--inventory` run refuses a remote Mac host. An SSH Mac server runs in a [campaign](sessions-campaigns.md#a-mac-dedicated-server).
- Remote clients and several actors are a [campaign](sessions-campaigns.md).
- A lost reply during a start or stop leaves the outcome unknown and keeps the host's lock.
- A run directory stays on its host, listed by `env status`, while its server may still run, when its fetch failed, or with `VALHEIM_TESTING_KEEP_RUNTIME=1`.
