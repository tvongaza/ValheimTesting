# This machine's inventory

What the toolkit finds on this machine when you give it no inventory file, what `valheim-test env list` and `env preflight` print, and how a file adds to it.

`EnvironmentInventory` is in [`Valheim.Testing.Game`](../packages/Valheim.Testing.Game.md). A file with environments on other machines is an [inventory file](sessions-inventory.md).

## This machine, with no file

With no inventory file, the inventory is this machine. That holds for `EnvironmentInventory.Read(null)`, a campaign that leaves out `inventory`, and `valheim-test env list` or `env preflight` without `--inventory`.

| Environment | Host | Install found from |
|---|---|---|
| `local-server` | `local` | Valheim Dedicated Server, Steam app 896660 |
| `local-client` | `local` | Valheim, Steam app 892970 (none on Linux) |

Steam's root is only ever the native default:

| OS | Steam root |
|---|---|
| Windows | the registered `HKCU\Software\Valve\Steam` `SteamPath` |
| macOS | `~/Library/Application Support/Steam` |
| Linux | `~/.steam/steam` |

The toolkit then reads every library in `steamapps/libraryfolders.vdf` and each app's `appmanifest_<id>.acf` `installdir`. An install counts only if its executable is there. If Steam is anywhere else, such as a Flatpak, list the environment with its `install` in an [inventory file](sessions-inventory.md).

ValheimTesting keeps its own state in a data folder: `%LOCALAPPDATA%\ValheimTesting` on Windows, `~/Library/Application Support/ValheimTesting` on macOS, `$XDG_DATA_HOME/ValheimTesting` (or `~/.local/share/ValheimTesting`) on Linux.

| Subfolder | Holds |
|---|---|
| `lock` | the host lock |
| `leases` | Steam-account leases |
| `journal` | the run journal |
| `runs/<environment>` | the environment's disposable profile runtime |

If a Windows install is on another volume than the data folder, its runtimes go to `<install volume>\ValheimTesting\runs`, because a profile hard-links the install's files. A no-game test moves the whole data folder with a [`FakeDataRoot`](../packages/Valheim.Testing.Game.md#test-fakes) scope.

The first environment gets ValheimCLI port 5688, the next 5689, and a server gets game port 2486. Each further environment takes the next free CLI port and a game port whose three-port range is clear. These avoid Valheim's standard 2456, so a personal server keeps its port and a crossplay lobby never takes its joins.

## env list and env preflight

```sh
valheim-test env list
valheim-test env preflight
```

Both print the same listing. A `detected:` line names each value found or assumed. A `NOT FOUND:` line names each thing missing, with every path tried. Then one line per environment: roles, host, install, runtime, ports, and for a client its selected architecture and available slices.

An eligible `env preflight` on a Windows PC prints lines in this shape. The paths are illustrative, and `<reason>` stands for the runtime reason the tool prints:

```text
detected: host local: this machine (windows)
detected: assumed host lock C:\Users\you\AppData\Local\ValheimTesting\lock
detected: Steam: C:\Program Files (x86)\Steam (from registry HKCU\Software\Valve\Steam SteamPath); libraries C:\Program Files (x86)\Steam
detected: Valheim (Steam app 892970): C:\Program Files (x86)\Steam\steamapps\common\Valheim
detected: Valheim Dedicated Server (Steam app 896660): C:\Program Files (x86)\Steam\steamapps\common\Valheim dedicated server
detected: local-server (server) on local: install C:\Program Files (x86)\Steam\steamapps\common\Valheim dedicated server; assumed runtime C:\Users\you\AppData\Local\ValheimTesting\runs\local-server (<reason>), ValheimCLI port 5688, game port 2486
detected: local-client (client) on local: install C:\Program Files (x86)\Steam\steamapps\common\Valheim; assumed client architecture x64, runtime C:\Users\you\AppData\Local\ValheimTesting\runs\local-client (<reason>), ValheimCLI port 5689
detected: assumed Steam-account leases on local in C:\Users\you\AppData\Local\ValheimTesting\leases
local-server: server on local; install C:\Program Files (x86)\Steam\steamapps\common\Valheim dedicated server; runtime C:\Users\you\AppData\Local\ValheimTesting\runs\local-server (<reason>); ValheimCLI port 5688, game port 2486
local-client: client on local; install C:\Program Files (x86)\Steam\steamapps\common\Valheim; runtime C:\Users\you\AppData\Local\ValheimTesting\runs\local-client (<reason>); ValheimCLI port 5689; selected architecture x64; available slices x64
ELIGIBLE: the inventory has a server and a client environment, and this machine's journal holds no run going or left unrecovered. The selected local host lock, game processes, and ValheimCLI ports passed their checks. The local client desktop and Steam process passed their checks. This process is not inside a packaged app. A session's inputs and host readiness are checked by valheim-test session check SESSION [--hosts].
```

`env list` gives no verdict. It exits 3 only for an inventory it cannot read.

`env preflight` adds one `REFUSED <actor> <check>: <reason>` line per failed check, then a verdict line that starts `ELIGIBLE` (exit 0) or `REFUSED` (exit 3). The checks:

| Check | Refuses when |
|---|---|
| `packaged app` | `valheim-test` runs inside a Windows packaged app |
| `run journal` | a run of another process is going, may be going, or was left unrecovered; or the journal is unreadable |
| `host` | a local host cannot be created from the inventory |
| `profile storage` | a Windows runtime cannot hard-link its install's files: another volume, or not NTFS |
| `host lock` | the host lock is not free |
| `client desktop` | a client has no interactive desktop session |
| `Steam session` | no Steam client is running for a client |
| `session` | a game process is already running on the host |
| `ValheimCLI port` | an environment's CLI port is in use |
| `client architecture` | a local Mac client cannot launch as its selected slice |

An inventory without a server or a client environment has no check line; only the verdict says so: `REFUSED: the inventory has no server environment.`

A missing dedicated server comes with three ways forward: install it from Steam (it is free), add a server environment, or run `valheim-test start` for a hosted local world. The command options are on the [`valheim-test` page](../packages/Valheim.Testing.NativeSmoke.md).

## A file that names some environments

A file lists the environments to use, in preference order, and gains none of this machine's unless it lists them. An environment on this machine needs only its name and role; install, runtime and ports are filled in as above:

```json
{ "name": "local-client", "roles": ["client"] }
```

Environments without a host go to the file's own `local` host. `leaseHost` and `leaseDirectory` default to this machine's only when every client is on it. Leave out `lock` and the leases to share this machine's defaults. A run's `result.json` records what was detected as `inventoryDetected`; a run with no file records `inventory: this machine`.

## Limits

- Steam outside its native root is not searched; name the install in a file.
- `env preflight` checks this machine only. `valheim-test session check SESSION --hosts` checks a session's other hosts.
- A running Steam process does not prove a signed-in account, so preflight does not claim one.
