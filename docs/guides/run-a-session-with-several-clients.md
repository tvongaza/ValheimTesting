# Run a session with several clients

Run one dedicated server and two or more owned clients together, each client on its own machine and Steam account, as one test.

## When to use this

Use it when a check needs more than one player: a hand-over between two clients, a second client seeing the first one's change, or a join and rejoin under load. One client and a server on this machine need none of this; see [test a save, restart and rejoin](test-save-restart-rejoin.md).

You need, for each client, a Windows or macOS machine with Valheim, Steam signed in to an account that owns the game, and a desktop session. Each client needs its own host and its own account.

## Steps

### 1. Describe the machines

Write an [inventory file](../reference/sessions-inventory.md) with one client environment per host. Name a `leaseHost` and `leaseDirectory`, where the Steam-account leases live. The tests' [sample inventory](../../tests/Valheim.Testing.NativeAcceptance/sample-environment-inventory.json) has a server and two clients on two Windows hosts.

### 2. Lock each role's files

Write a dependency lock per role, so a server-only mod never reaches a client:

```sh
dotnet run scripts/native-dependencies.cs -- resolve /private/test/request.json /private/test/locks/server.json
```

Run it from a ValheimTesting checkout, once per role ([resolve dependencies](../reference/game-run-inputs.md#resolve-mod-dependencies)).

### 3. Register a character per client

Register a different game-created local character for each client in a `DisposableCharacterStore`. One character copied twice is still one player.

### 4. Write the session manifest

Save it outside the repository, for example as `session.json`. The paths are illustrative:

```json
{
  "inventory": "./environments.json",
  "world": "./fixture-world",
  "join": "test-server.example:2456",
  "server": { "dependencyLock": "./locks/server.json" },
  "clients": {
    "client-a": { "dependencyLock": "./locks/client-a.json",
      "character": { "store": "./characters/a", "registeredName": "tester-a", "fileName": "vt-campaign-a" } },
    "client-b": { "dependencyLock": "./locks/client-b.json", "differentHostFrom": ["client-a"],
      "character": { "store": "./characters/b", "registeredName": "tester-b", "fileName": "vt-campaign-b" } }
  }
}
```

[Campaigns](../reference/sessions-campaigns.md#the-session-manifest) lists every field.

### 5. Check the session

```sh
valheim-test session check session.json --hosts
```

It reports every problem in one pass, then `READY` when the hosts pass. It reads the installs, ports, free space, signed-in Steam accounts and run journals, and changes nothing.

<!-- TODO(docs): capture output of `valheim-test session check session.json --hosts` -->

### 6. Open the clients in your scenario

Pass the plan's client sections by name to the runner:

```csharp
return await PinnedServerRun.RunCampaignAsync(manifestFile, plan,
    plan => new Dictionary<string, ClientRunPlan> { ["client-a"] = plan.Client!, ["client-b"] = plan.SecondClient! },
    output, options);
```

In the scenario, open both at once, then join each:

```csharp
var clients = await session.OpenClientsAsync(new Dictionary<string, ClientRunPlan>
{
    ["client-a"] = plan.Client!, ["client-b"] = plan.SecondClient!,
});
```

The [three-actor scenario](../../tests/Valheim.Testing.NativeAcceptance/ThreeActorSmokeScenario.cs) joins, leaves and rejoins two clients.

## Check the result

- Each client writes its evidence into its own folder, `<output>/client-a/` and `<output>/client-b/`.
- `prepared/environment-assignments.json` names the host each actor ran on.
- `valheim-test env status --inventory environments.json` exits 0 when every lease, lock and profile was released.

## Problems

| Symptom | Fix |
|---|---|
| Two clients placed on one host are refused | Add a client environment on another host, or `differentHostFrom` |
| A client is refused because its account is leased | Wait for the holder run, or `valheim-test env recover --run ID` once it is over |
| Two clients are refused on one Steam account | Sign each client host in to its own account |
| A client stops at once with "Logged In Elsewhere" | Someone plays on that account elsewhere; end that session and start Steam on the host again |
| A local client is refused for its CLI port | Use the port of the environment the client was assigned |
| A client is refused for `-savedir` | Remove it from the client's launch arguments |

## Related

- [Campaigns](../reference/sessions-campaigns.md)
- [Locks, leases and recovery](../reference/sessions-leases-and-recovery.md)
- [Game sessions](../reference/sessions-game-session.md)
