# Game hosts

A game host is a machine a run works on: this machine, a machine over SSH, or a Linux container. Every host runs the same scripts in its OS's one shell, so a run behaves the same everywhere.

The types are in `Valheim.Testing.GameSessions`. You name hosts in an [inventory file](sessions-inventory.md); the runner builds the `IGameHost` for each.

## Host kinds

| Host | `kind` | `shell` | Server | Client |
|---|---|---|---|---|
| Windows | `local`, or `ssh` (OpenSSH Server) | `pwsh` | Yes | Only in the signed-in desktop session |
| Linux | `local` or `ssh` | `bash` | Yes | No |
| macOS | `local`, or `ssh` (Remote Login) | `bash` | Native arm64; over SSH only in a campaign | In the unlocked console session |
| Linux container | `container` (`docker exec -i`) | `bash` | Yes | No |

`LocalGameHost`, `SshGameHost` and `ContainerGameHost` implement `IGameHost`. [Platforms](platforms.md) lists the executables, and [host shells](platforms.md#host-shells) says why each OS has one shell.

| Host | Tools its scripts use |
|---|---|
| Windows | PowerShell 7, `System32\tar.exe` |
| Linux | bash, coreutils (`base64`, `mkfifo`, `sha256sum`, `tail`), `find`, `setsid`, `tar`, `/proc` |
| macOS | bash, `shasum`, BSD `tail` and `tar`; `launchctl` access to the user's GUI domain for a remote client |

The [Linux server image](../../docker/linux-server/README.md) and Ubuntu have the Linux tools.

## IGameHost

| Operation | Member | Contract |
|---|---|---|
| Run a script | `RunAsync(script, variables, timeout)` | The script travels on standard input; each variable arrives as a quoted literal. The real exit code comes back |
| Take the host lock | `AcquireLockAsync(lockPath, owner, timeout)` | Creates the owner file exclusively; exactly one run wins, and nothing waits or retries |
| Ship a revision or files | `ShipRevisionAsync(repository, revision, dir, timeout)`, `ShipFilesAsync(localDir, dir, timeout)` | A tar checked by SHA-256 on the host, extracted into a new directory |
| Follow a log | `LogOffsetAsync(log, timeout)`, then `WaitForLogAsync(log, offset, success, failures, timeout)` | Event driven on the host; take the offset before the action whose line you await |
| Fetch evidence | `FetchDirectoryAsync(dir, localDir, timeout)` | A tar checked on both ends, extracted into a new local directory |
| Reach ValheimCLI | `OpenCliTunnelAsync(cliPort, readyTimeout, localPort)` | A loopback tunnel ([below](#reach-valheimcli)) |

A host result is `Exited` with the script's code, `TransportFailed`, or `Unknown`. A deadline always gives `Unknown`: the script may not have run, may have finished, or may still run. Nothing is retried, because a change must never be repeated blindly.

A lock claim that timed out is `HostLockState.Unknown`, neither yours nor free. Another run's lock throws `HostLockException` naming its holder.

To review a host install's pins before a run, list it on the host: `HostInstall.ListAsync(host, install, timeout)` returns every file's SHA-256 in `Files`, and `HostInstall.Pins(listing)` computes the pins.

## SSH

SSH uses keys or an agent only. BatchMode is always on, a password is never accepted, and the host key must already be known.

| Inventory field | Does |
|---|---|
| `destination` | `user@host`, `ssh://user@host:port` or an ssh-config alias |
| `port` | The port; 0 leaves it to the destination or the ssh config |
| `sshOptions` | Extra `-o` options as `Name=value`, for example `IdentityFile=~/.ssh/valheim_tests` |
| `ssh` | The OpenSSH client executable; `ssh` by default |
| `connectSeconds` | The connect timeout; 10 by default |

Each `sshOptions` value is taken literally. A value with spaces, quotes or a leading `#` is passed quoted. An empty value, or one with a tab or line break, is refused. A value that needs an escape, such as a `"` or a UNC path, needs OpenSSH 8.7 or later.

### Which ssh client runs on Windows

| The runner runs | Default client |
|---|---|
| In a desktop session | The built-in OpenSSH `ssh` from PATH, which reaches keys in the Windows ssh-agent service |
| Inside an SSH session | Git for Windows' `ssh.exe` and `scp.exe` |

The runner counts as inside an SSH session when `SSH_CONNECTION` or `SSH_CLIENT` is set. There, the built-in client can hang on a script sent through redirected stdin. Git's client cannot reach a forwarded agent, so give such a host a key file (`IdentityFile=`).

If Git's pair is missing in an SSH session, the host refuses before staging and names the fix. An explicit `ssh` path always wins; the bare names `ssh` and `ssh.exe` ask for the default. A server run records the choice as `serverHostSshClient`. macOS and Linux use `ssh` from PATH.

## Reach ValheimCLI

ValheimCLI listens on its host's loopback only. For an SSH host, `OpenCliTunnelAsync` starts `ssh -N -L 127.0.0.1:<local>:127.0.0.1:<cliPort>`, so neither end is bound beyond loopback. Connect to `127.0.0.1:tunnel.LocalPort`.

| Host | ValheimCLI is reached at |
|---|---|
| `local` | Its own port |
| `ssh` | The tunnel's local port: `localCliPort`, or a free one when 0 |
| `container` | Its own port, only with `--network host` on a local Docker daemon |

The tunnel refuses a local port already in use. It also refuses a host whose ssh config adds `LocalForward`, `RemoteForward` or `DynamicForward` entries, which could listen beyond loopback.

## Servers on a host

`HostServer.StartAsync(host, launch, bootDirectory, timeout)` starts an owned dedicated server and returns a `HostServerProcess`, identified by process ID and start time. Stopping it sends SIGINT, or Ctrl+Break on Windows, and kills it only after the quit deadline.

| Host | How the server starts | `stdout.log`, `stderr.log` |
|---|---|---|
| Linux | In its own session (`setsid`) under a small recorder | Kept in the boot directory |
| macOS | Under a detached supervisor | Kept in the boot directory |
| Windows | From a short-lived scheduled task, independent of the asking session | Not recorded |

On Windows the task's logon depends on the asking session:

| Asking session | Task | Server runs in |
|---|---|---|
| Elevated, such as an administrator over SSH | S4U | Session 0, with no desktop |
| The user's own single desktop session | That session's interactive token | That desktop, with its console window; signing out stops it |
| Anything else | Refused in preflight, before staging | |

The task is removed once the run acknowledges the server's PID. If a start gives up first, the launcher's whole process tree is killed before the task stops, so a game it spawned never outlives the start. `boot-N.process.json` records the logon as `taskLogon`. No Steam client is needed.

## Clients on a host

A game client needs the signed-in user's desktop session: its display, GPU and Steam client. `InteractiveClient.StartAsync(host, launch, launchDirectory, timeout)` starts one inside that existing session. It returns an `InteractiveClientProcess` identified by process ID and start time.

```csharp
var launch = GameLaunch.ForClient(@"C:\Games\Valheim", ["+connect", "server.example:2456"], hostPlatform: ClientPlatform.Windows,
    secretVariables: [plan.PasswordVariable!]);
await using var client = await InteractiveClient.StartAsync(new SshGameHost("gaming-pc", "tester@gaming-pc.example", HostShell.Pwsh), launch,
    @"C:\ValheimTesting\runs\run-42\client", TimeSpan.FromMinutes(2));
```

| Host | How the client starts |
|---|---|
| Windows | A scheduled task with "Run only when user is logged on", named `ValheimTesting-client-<id>`, always removed again |
| macOS | A run-owned LaunchAgent in the console user's GUI domain, through `/usr/bin/arch` with the selected slice |

The start refuses with `InteractiveSessionException` before anything starts:

| `InteractiveRefusal` | When |
|---|---|
| `NoSession` | The user has no desktop session there, or more than one |
| `NoSteam` | No Steam client runs as the user in that session |

A missing install file, an existing launch directory or a host of the wrong shell is refused too. `StopAsync` and disposal kill only that process, and only while its start time still matches.

A secret such as the join password is named in `secretVariables`. Its value never goes on a command line or into the script, and it is redacted from every reply. Other `environment` values are not secret.

In a campaign you never call this yourself: `session.OpenClient` starts the client on its assigned host ([campaign clients](sessions-campaigns.md#clients)).

## Limits

- `GameLaunch.ToStartInfo` and `ClientSession.Launch` start processes on this machine only.
- A server in a container has not run a Valheim server natively this way.
- A remote Mac client is checked natively only with the runner on the same Mac.
- A Windows server's stdout and stderr are not recorded, so its log scan lists no stdout.
