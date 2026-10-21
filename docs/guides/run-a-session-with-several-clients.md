# Run a session with several clients

Place one marker, then require two clean clients to observe it independently. Make B leave and rejoin while A stays in the pinned world.

Excerpts from the canonical [marker scenario](../../examples/FullLifecycle/ExampleMod.Tests/MarkerScenario.cs):

```csharp
report.Step("the mod marks the dry site", () => server.Execute(Mark(plan.DrySite)).RequireLine("OK: marked ", "ExampleMod did not mark the dry site"));
```

```csharp
report.Step("A sees exactly one marker", () => RequireClientMarkers(a, plan.DrySite, 1));
report.Step("B independently sees exactly one marker", () => RequireClientMarkers(b, plan.DrySite, 1));
```

The scenario also requires zero markers before acting, the wet-site refusal, and complete server counts. A passing reply from the mod does not satisfy these observations.

Captured result fields:

```json
{
  "Name": "B sees exactly one marker after rejoin",
  "Passed": true
}
```

## When to use this

Use this for replication to simultaneous players or rejoining while another player remains connected. For persistence across a server restart, use [Test a save, restart and rejoin](test-save-restart-rejoin.md).

Start with an existing two-client campaign and two registered disposable characters. `server-load` currently derives one client role and one seed character.

<!-- TODO(docs): verify optional second-role bootstrap after #901 -->

Each client needs its own host and Steam account. See [Platforms](../reference/platforms.md) and the [native-run checklist](before-a-native-run.md).

## Steps

### 1. Describe the machines

Prepare an [inventory](../reference/sessions-inventory.md) with a client environment on each host. Keep the server address reachable from both clients. The toolkit discovers signed-in accounts and refuses conflicts; it does not sign Steam in.

### 2. Lock each role's files

Keep the tool-generated dependency locks and adapter from the [persistence setup](test-save-restart-rejoin.md#1-prepare-the-mod-and-adapter). Both clients exclude the production mod. Reuse a clean-client lock when its file set is identical; do not replace hashes by hand.

### 3. Register a character per client

Use two [registered disposable test characters](xref:Valheim.Testing.Game.DisposableCharacterStore). Copies of one seed retain the same player ID and cannot represent simultaneous players.

### 4. Write the session manifest

Use the existing [session manifest](../reference/sessions-campaigns.md#the-session-manifest) with roles `client` and `client-b`. Give B its own registered character and constrain it to another host. Keep the original fixture, server adapter, role locks and inventory.

Use the same [three-file consumer map](../../examples/FullLifecycle/consumer/README.md#native-persistence-entry). Put B's client plan in `second-client-plan.json` beside the original `client-plan.json`.

In `marker-plan.json`, select `dry-site-two-clients` and declare `SecondArrival` from independently measured terrain. Keep the original dry/wet sites and A arrival. Require dry arrivals 3–20 metres from the same marker and at least 3 metres apart.

The entry binds both plan sections to the named campaign roles. `dry-site-lifecycle` refuses second-client settings; `dry-site-two-clients` requires both client sections and arrivals.

### 5. Check the session

From the independent campaign folder:

```sh
valheim-test session check campaign.json --hosts --json
```

The check reads the hosts, installs, ports, accounts and registered character identities before a game starts.

Captured result field:

```json
{
  "Ready": true
}
```

### 6. Open the clients in your scenario

From the generated consumer folder, run the unchanged entry:

```sh
dotnet run --project SmokeCheck.csproj -c Release -- RUN_OUTPUT/campaign.json NEW_RESULT_DIRECTORY
```

The [canonical scenario](../../examples/FullLifecycle/ExampleMod.Tests/MarkerScenario.cs) calls `GameSession.OpenClientsAsync` with the same named plan objects used by campaign binding. The toolkit owns both clients and their cleanup.

Captured final line:

```text
PASS
```

## Check the result

Require separate A and B marker checks, two connected peers, A still pinned while B leaves, one peer during B's absence, and two peers after rejoin. Require B's complete marker census again after rejoin.

Inspect `result.json`, `junit.xml`, each client's command trace, and `prepared/environment-assignments.json`. Finish with `valheim-test env status --inventory environments.json`; it must list nothing your run left.

## Problems

| Symptom | Fix |
|---|---|
| Duplicate player identity | Register another game-created disposable character |
| Account or host conflict | Use distinct client hosts and signed-in accounts |
| B has an incomplete observation | Inspect B's trace; incomplete does not mean zero |
| Invalid arrival | Measure dry ground beside the same marker |
| Cleanup fails | Follow [recovery](../reference/sessions-leases-and-recovery.md) |

## Limits

This visibility mode does not prove save/restart persistence. Captured results use published toolkit `0.1.0`, fixture UID `3010640879`, a macOS server/client A and Windows client B. Published `0.2` verification remains pending.

## Related

- [Standalone consumers and scripted controls](../../examples/FullLifecycle/consumer/README.md)
- [Campaigns](../reference/sessions-campaigns.md)
- [Game sessions](../reference/sessions-game-session.md)
