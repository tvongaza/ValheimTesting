# Inventory file

An inventory file lists your machines (hosts) and, in preference order, the game installs on them (environments) a run may use. A run with `--inventory`, or a campaign that names one, places its actors from it.

`EnvironmentInventory.Read(path)` reads it; the type is in `Valheim.Testing.Game`. With no file, the inventory is this machine alone ([this machine's inventory](game-inventory.md)). The file describes one person's machines, so keep it beside your tests, not in the repository.

## Example

These hosts and paths are illustrative:

```json
{
  "hosts": {
    "linux-box":  { "kind": "ssh", "platform": "linux", "shell": "bash", "destination": "tester@linux-box.example",
                    "sshOptions": ["IdentityFile=~/.ssh/valheim_tests"], "lock": "/var/tmp/valheim-testing/lock" },
    "gaming-pc":  { "kind": "ssh", "platform": "windows", "shell": "pwsh", "destination": "tester@gaming-pc.example",
                    "lock": "C:\\ValheimTesting\\lock" },
    "server-ctr": { "kind": "container", "platform": "linux", "shell": "bash", "container": "valheim-server", "user": "valheim",
                    "lock": "/home/valheim/lock" }
  },
  "environments": [
    { "name": "box-server", "host": "linux-box", "roles": ["server"], "install": "/opt/valheim/server",
      "runtime": "/srv/valheim-testing/runs", "cliPort": 5577, "gamePort": 2466 },
    { "name": "pc-client", "host": "gaming-pc", "roles": ["client"], "install": "C:\\Games\\Valheim",
      "runtime": "C:\\ValheimTesting\\runs", "cliPort": 5578, "localCliPort": 6578 }
  ],
  "leaseHost": "linux-box",
  "leaseDirectory": "/var/tmp/valheim-testing/leases"
}
```

The tests' [sample inventory](../../tests/Valheim.Testing.NativeAcceptance/sample-environment-inventory.json) has two Windows hosts.

## Hosts

| Field | Holds |
|---|---|
| `kind` | `local`, `ssh` or `container` |
| `platform` | `windows`, `linux` or `macos`: the host's own OS; a container's is `linux` |
| `shell` | `pwsh` on Windows, `bash` on macOS and Linux |
| `lock` | The host lock's directory, an absolute path on the host |
| `destination`, `port`, `sshOptions`, `ssh`, `connectSeconds` | An `ssh` host's connection ([SSH](sessions-hosts.md#ssh)) |
| `container`, `user`, `docker` | A `container` host's name or id, the user scripts run as, and the Docker CLI |

A lock is per host, so two containers on one machine are two hosts. A `local` host must describe this machine's platform.

## Environments

| Field | Holds |
|---|---|
| `name` | The environment's name |
| `host` | A host from `hosts` |
| `roles` | `["server"]` or `["client"]` |
| `install` | The game install on the host, absolute; only read |
| `runtime` | Where the host keeps [disposable profiles](sessions-profiles.md) |
| `cliPort` | ValheimCLI's port on the host |
| `localCliPort` | An `ssh` environment's tunnel port on this machine; 0 picks a free one |
| `gamePort` | A server environment's game port |
| `loaderPackage` | A [reviewed loader package](game-run-inputs.md#pin-a-bepinex-loader) for this environment |
| `architecture` | A client's slice: `x64`, or `arm64` on macOS ([Apple Silicon](platforms.md#native-apple-silicon-client)) |

| Top-level field | Holds |
|---|---|
| `leaseHost` | The host that keeps [Steam-account leases](sessions-leases-and-recovery.md#steam-account-leases) |
| `leaseDirectory` | The lease folder on that host, absolute |

Inventories that may use the same Steam accounts must name the same `leaseHost` and `leaseDirectory`. Left out, the leases are this machine's, which works only when every client is on it.

## What Read refuses

`EnvironmentInventory.Read` checks everything at once, before anything starts:

- an unknown field, including any credential or Steam ID field, without repeating its value;
- an unknown kind, platform or shell, or a shell that does not match the platform;
- an ssh destination or option `SshGameHost` would refuse, such as a password or a port given twice;
- a relative path, or one not in the host's own style;
- a client environment on a Linux host;
- a macOS server environment on a host that is not `local` or `ssh`;
- an `architecture` on a server environment, or `arm64` for a client that is not on macOS;
- an environment listed twice, or a ValheimCLI port outside 1024 to 65535;
- `roles` other than `["server"]` or `["client"]`, or a `gamePort` on a client environment;
- a missing `leaseHost` or a relative `leaseDirectory` when a client environment is on another machine.

A `runtime` that is the install, lies inside it or holds it is refused on the host, when a run prepares its profile there.

## How actors are placed

| Run | Places |
|---|---|
| `PinnedServerRun --inventory` | Its server on the first server environment that fits the plan ([a server on another host](sessions-pinned-server-run.md#a-server-on-another-host)) |
| A campaign | Each declared actor, by its role's `environmentCandidates` and `differentHostFrom` ([campaigns](sessions-campaigns.md#place-the-actors)) |

Placement refuses:

- two game clients on one host;
- one ValheimCLI port used twice on one host;
- two actors reached on the same port of this machine (a `local` or `container` host's `cliPort`, or an `ssh` environment's `localCliPort`).

## Limits

- The inventory holds no credentials and no Steam IDs. A client's identity is the account signed in on its host.
- A busy host refuses the run; nothing picks another host or waits.
