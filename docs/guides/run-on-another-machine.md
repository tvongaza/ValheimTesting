# Run the dedicated server on another machine

Run your mod's pinned server test with the server on a Windows PC, a Linux box or a Linux container, while you work on another machine.

## When to use this

Use it when your server test runs through [`PinnedServerRun`](../reference/sessions-pinned-server-run.md) and the server should run elsewhere. The same runner and plan work on this machine and on a host; you add one file and one option. For clients on other machines too, use a [campaign](run-a-session-with-several-clients.md).

## Steps

### 1. Prepare the host

| Host | Needs |
|---|---|
| Windows | OpenSSH Server, [PowerShell 7](../reference/platforms.md#host-shells), the Valheim Dedicated Server install. Sign in over SSH as an administrator, or run from the user's one desktop session |
| Linux | SSH, bash, coreutils, `setsid`, `tar`, the Valheim Dedicated Server install |
| Linux container | The [Linux server image](../../docker/linux-server/README.md), run with `--network host` on a local Docker daemon |

On every SSH host, log in once by hand so its host key is known, and use a key or an agent: the toolkit never sends a password. On Windows, put the run folder on the install's NTFS volume ([where a profile lives](../reference/sessions-profiles.md#where-a-profile-lives)).

### 2. Write the inventory

Save this beside your tests as `environments.json`, outside the repository. The names and paths are illustrative:

```json
{
  "hosts": {
    "pc": { "kind": "ssh", "platform": "windows", "shell": "pwsh", "destination": "tester@gaming-pc.example",
            "lock": "C:\\ValheimTesting\\lock" }
  },
  "environments": [
    { "name": "pc-server", "host": "pc", "roles": ["server"], "install": "C:\\vt\\install", "runtime": "C:\\vt\\runs",
      "cliPort": 5577, "gamePort": 2466 }
  ]
}
```

For a Linux host, use `"platform": "linux"` and `"shell": "bash"`. For a container, use `"kind": "container"`, `"container"` and `"user"` instead of `destination`. [Inventory file](../reference/sessions-inventory.md) lists every field.

Match the environment to your plan: its `cliPort` is the plan's `port`, and its `gamePort` is the plan's `-port` argument.

### 3. Validate without launching

Run from your runner's project folder. `--inventory` goes before the mode:

```sh
dotnet run -c Release -- --inventory ../environments.json validate plan.json <new-output-directory>
```

It checks the plan, takes the host's lock, stages the server's profile on the host and ships the world, then stops. It prints `VALIDATED` and exits 0.

### 4. Run

```sh
dotnet run -c Release -- --inventory ../environments.json run plan.json <new-output-directory>
```

It prints `PASS` or `FAIL`. The output holds `result.json`, `junit.xml`, each boot's logs fetched into `boot-N/`, and the saved world in `host-world/`.

## Check the result

- `serverEnvironment` in `result.json` names the environment chosen and why each other was skipped.
- `valheim-test env status --inventory ../environments.json` reads every host's journal and exits 0 when nothing was left.

```text
N runs ended and left nothing. Nothing was changed.
```

## Problems

| Symptom | Fix |
|---|---|
| No environment fits the plan | Match `cliPort` to the plan's `port` and `gamePort` to its `-port` |
| A Windows host fails every script, naming an install command | Install PowerShell 7 ([host shells](../reference/platforms.md#host-shells)) |
| A Windows host is refused in preflight for its session | Connect as an administrator, or run from the user's single desktop session |
| The host is refused because another run holds its lock | Wait for that run, or `valheim-test env recover --run ID` once it is over |
| Inside an SSH session on Windows, ssh hangs or finds no key | Give the host `"sshOptions": ["IdentityFile=..."]` ([which ssh client runs](../reference/sessions-hosts.md#which-ssh-client-runs-on-windows)) |
| `UNKNOWN`, exit 3 | A reply was lost; check `valheim-test env status` before running again |
| The runtime is refused on Windows | Move `runtime` to the install's NTFS volume |

## Related

- [Profiles and hosts](../concepts/profiles-and-hosts.md)
- [Game hosts](../reference/sessions-hosts.md)
- [Pinned server runner](../reference/sessions-pinned-server-run.md#a-server-on-another-host)
- [Platforms](../reference/platforms.md)
