# Campaigns

A campaign is a run with several actors placed from an inventory: one dedicated server or none, and named clients, on this machine or others. A session manifest declares the actors; the toolkit prepares a profile for each, leases its Steam account and runs your plan on them.

`PinnedServerRun.RunCampaignAsync` runs one, and `valheim-test session check` checks one. [Run a session with several clients](../guides/run-a-session-with-several-clients.md) walks through it.

## The session manifest

These paths are illustrative:

```json
{
  "inventory": "environments.json", "world": "fixture-world", "join": "linux-box.example:2466",
  "server": { "dependencyLock": "locks/server.json" },
  "clients": { "player": { "dependencyLock": "locks/client.json",
    "character": { "store": "characters", "registeredName": "tester", "fileName": "vt-player" } } }
}
```

| Field | Holds |
|---|---|
| `inventory` | The [inventory file](sessions-inventory.md); left out, this machine alone |
| `world` | The pinned world fixture |
| `worldUid` | The fixture's reviewed world UID; another fixture is refused before copying |
| `join` | The server's game address with its port; none without a server |
| `server` | The dedicated server's role; left out, one client hosts the world |
| `clients` | The named clients' roles |

Relative paths are relative to the manifest. `HostedCampaignManifest.Read` refuses an unknown field.

| Role field | Holds |
|---|---|
| `dependencyLock` | The role's reviewed dependency lock: the mod, its dependencies and one pinned ValheimCLI ([resolve dependencies](game-run-inputs.md#resolve-mod-dependencies)) |
| `environmentCandidates` | Inventory environments to try, in order; empty tries every environment for the role |
| `differentHostFrom` | Actor names that must run on another host |
| `loaderPackage` | A reviewed loader package for this role's profile |
| `steamRuntimePackage` | A Mac dedicated server's SteamCMD libraries |
| `architecture` | A client's slice; empty inherits the environment's |
| `files` | Extra config, script or asset files for the profile |
| `character` | A client's disposable character: `store`, `registeredName`, `fileName` |

Each role has its own lock, so a server-only mod never reaches a client by accident.

## Place the actors

Each actor gets an environment of its role from the inventory, by its `environmentCandidates` and `differentHostFrom`. If an earlier choice leaves a later actor without a host, the placement backs up and tries another. The output's `prepared/environment-assignments.json` records the choice; the observed Steam IDs are never written.

## Check before a run

```sh
valheim-test session check SESSION [--hosts] [--json]
```

| Check | Without `--hosts` | With `--hosts` |
|---|---|---|
| Manifest, locks, characters, fixture, inventory | Yes | Yes |
| Actor placement | Yes | Yes |
| Installs, loaders, ports and free space on each host | | Yes |
| Signed-in Steam accounts, and the account playing elsewhere | | Yes |
| Every host's run journal, the lease host's included | | Yes |
| Conflicting games | | Yes |

It reports every independent problem in one pass and never copies or launches anything. It refuses while another run is `LIVE` on one of the campaign's hosts, left something there or on the lease host, or may still run elsewhere. A Valheim client already running where a campaign client needs the desktop session is a conflict.

<!-- TODO(docs): capture output of `valheim-test session check SESSION --hosts` -->

## Run it

```csharp
return await PinnedServerRun.RunCampaignAsync(manifestFile, plan, clients, output, options);
```

`clients` maps each client name to its plan section. The run:

1. checks the plan against the campaign;
2. takes one lock per host and prepares every actor's profile concurrently, after the full preflight passes;
3. binds the plan to the prepared actors: installs, ports, runtime, world, pins and characters;
4. runs the plan as [the pinned runner](sessions-pinned-server-run.md#what-a-run-does) does;
5. retires the characters, then each profile, then releases the leases and the host locks.

`HostedCampaignPreparation.PrepareAsync` and `InspectAsync` expose the preparation and the check to your own code.

## Clients

`session.OpenClient(plan.Client, "player")` starts an owned client on its assigned host. On another machine, it starts in that host's desktop session ([clients on a host](sessions-hosts.md#clients-on-a-host)). The client's host lock is held until teardown, and ValheimCLI is reached through its host's tunnel.

| Rule | Detail |
|---|---|
| Evidence | Each client writes into its own folder `<output>/<name>/` |
| A client on this machine | Must use its role's CLI port; a plan naming another port is refused |
| `-savedir` | Refused in a campaign client's launch arguments: its character and world are staged in the default save folder |
| A client on a Linux host | Refused: Linux hosts run the dedicated server only |
| Steam account | Leased before anything else on its host ([leases](sessions-leases-and-recovery.md#steam-account-leases)) |

Sign into Steam on each client host before the run. The runner never signs in or handles a Steam password.

## Characters

Each client stages its disposable character into its host user's `characters_local`. It first checks that host's Steam `userdata` for a same-named Steam Cloud character, and refuses one.

| Host | `characters_local` |
|---|---|
| Windows | `%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\characters_local` |
| macOS | `~/Library/Application Support/IronGate/Valheim/characters_local` |

`session check --hosts` prints both resolved folders. Set `charactersLocalDirectory` or `steamUserDataDirectory` on the character only for a non-standard place. Two simultaneous clients need different player IDs, so register two characters; one seed copied twice is still one player.

## A campaign without a dedicated server

Leave out `server` and `join`. One client hosts the world and the others are its peers. The manifest's `world` is the hosted fixture.

In the plan, exactly one client section has `hostWorld`, and every other sets `joinsHost` (`HostedCampaignPreparation.CheckHostedPlan`). Run it with `PinnedServerRun.RunCampaignAsync(manifest, plan, clients, output, HostedRunOptions<TPlan>)`. The session's `Host` is the hosting client and its `Clients` are the peers.

The hosting client's world is placed in its host user's own `worlds_local` through the journalled placement:

1. Refuses any entry already named for the world: `<name>`, `<name>.*` or `<name>_backup*`, in any case.
2. Journals the placement, ships the verified fixture into `<runtime>/<runId>/host-world-stage`, and checks every file.
3. Moves the world's entries into `worlds_local`.
4. Once no client can host it, moves every entry named for the world, backups included, into `<runtime>/<runId>/host-world`, and fetches it into the evidence.

Nothing in the user's worlds is ever deleted. `hostWorldOn` in the provenance names the folder.

## A Mac dedicated server

Give the inventory a `macos`/`bash`/`ssh` host, and a server environment pointing to that Mac's SteamCMD server install. The server role adds two reviewed manifests:

```json
"server": {
  "dependencyLock": "locks/server.json",
  "loaderPackage": "locks/native-mac-loader.json",
  "steamRuntimePackage": "locks/steam-runtime.json"
}
```

Capture `steam-runtime.json` on the Mac ([Steam runtime package](platforms.md#steam-runtime-package)), then copy it to the runner. `session check --hosts` checks the manifest, the arm64 loader, free space and the host session before any write.

## Limits

- Two hosts signed in to one test account have not been run natively.
- Rented GPU VM clients are experimental.
- A crossplay host's peer join is refused.
