# Locks, leases and recovery

A run claims what it uses and journals what it makes, so an interrupted run can be cleaned up by proof, never by guessing. This page covers the host lock, Steam-account leases, the run journal, and what `valheim-test env status`, `env recover` and `env teardown` do with them.

The commands and their options are on the [`valheim-test` page](../packages/Valheim.Testing.NativeSmoke.md#env). [Debug a native run](../guides/debug-a-native-run.md#clean-up-what-a-run-left) says which one to use.

## The host lock

Every run takes a host's lock before it touches that host, and holds it until its processes there are proven stopped. The lock is an owner file created exclusively in the host's `lock` directory, so exactly one run wins.

- Another run's lock refuses this run at once, naming the holder. Nothing waits or retries.
- A lock whose release cannot be proven stays held. `env recover` releases it once nothing of the run is left on that host.
- On this machine the lock is in ValheimTesting's data folder ([this machine's inventory](game-inventory.md#this-machine-with-no-file)).

## Steam-account leases

Steam allows one running session per account. A campaign reads the account signed in on each client host, refuses two clients on one account, and leases each account for the run. You list no Steam IDs.

| Rule | Detail |
|---|---|
| Where | Claim files under `<leaseDirectory>/<pool>/<account>` on the inventory's `leaseHost`, keyed by an opaque hash of the account |
| Claim | Exclusive creation of the account's next numbered claim file; exactly one claimer wins |
| Release | Only by the holder's lease id, by `env recover` or `env teardown --run ID`, or by `env teardown --run ID --machine-gone` |
| Expiry | None: a lease never lapses on a timer |
| Refusal | A held account refuses the client at once, naming the holder run and `env recover --run ID` |

Reports and exceptions carry the opaque lease key, never a SteamID. A session's client holds its account as a `SteamAccountHold`, which the session acquires and releases.

For each client, a campaign runs these as steps:

1. `lease a Steam account for client <name>`, before anything else on the client's host.
2. Check that the host is still signed in to the leased account. Another account, none, or an unreadable state is refused before launch.
3. Hold the lease for the whole run.
4. At teardown, after the client stopped, release the lease. A client whose stop is unproven keeps its lease (`lease-kept`) until `env teardown --run ID` proves it stopped.

| Host | Signed-in account read from |
|---|---|
| Windows | `HKCU\Software\Valve\Steam\ActiveProcess\ActiveUser` |
| macOS | `~/Library/Application Support/Steam/config/loginusers.vdf`: the `MostRecent` user, else the single newest `Timestamp` |

### The account playing on another computer

Leases keep the toolkit's own runs apart; a person can still be playing on the account. Before a campaign, `session check --hosts` asks every other client host of the inventory whether Valheim runs there as the host's user. If it does, that host's signed-in account must be none a client would use. An unreadable answer is refused: unknown is never a pass.

When a client starts, its host's Steam `logs/connection_log.txt` is followed from the launch. If Steam logs "Logged In Elsewhere", the start ends at once with that reason. The tool never signs in, answers a Steam prompt, or starts or stops Steam.

## The user's files

The game writes some files into the user's own data folder whatever the launch asks:

| Process | Files |
|---|---|
| Every game client | The recent-servers list (`serverlist_local/recent` and `recent.old`) |
| A macOS dedicated server | Its access lists, beside its world |

Before an owned process starts, the run backs those files up into `user-files-<run>-<label>` in the evidence, journals the backup and moves the files aside. Once the process is proven stopped, the user's versions go back, and the run's versions stay in the backup's `after` folder. After an unproven stop, `env recover` restores them from the journal.

One run at a time captures a folder's files. A second run's capture refuses, changing nothing, until the first one's restore or recovery.

## The run journal

Each run journals on every host it uses, beside the host's lock, in `journal/<runId>/<actor>.jsonl`. A line goes in before anything is made, and another once it is made and when it is retired. An entry the host cannot write stops the run before that copy, character or process.

| Entry | Written |
|---|---|
| `copy-intended`, `copy-done` | Before and after a profile, world copy or character is made |
| `copy-retired`, `copy-kept` | When it is removed or handed over, or kept on purpose |
| `process-intended`, `process-started` | Before and after each launch, with its pid file and the SHA-256 of its command line |
| `lease-released`, `lease-kept` | When a Steam lease is released or kept |
| `run-ended` | The run's end, once per journal |
| `cleanup-abandoned` | Instead of the end, after a second Ctrl+C |
| `run-recovered` | After `env recover` on a host |

`run-ended` has one state word and `cleanupVerified`:

| State | Means |
|---|---|
| `passed`, `failed`, `unknown` | How the run ended |
| `recovered: runner gone` | `env recover` proved the runner gone and nothing left |
| `abandoned: its machine is gone` | `env teardown --machine-gone` released its leases |

A run refused before it journalled anything gets no entry. A run killed outright writes no end; `env recover` records it.

Never edit, move or archive a journal file to settle a verdict. Resolve the process, lock or line the command names.

## env status

`valheim-test env status [--inventory FILE] [--json]` reads the journal of every host in the inventory and changes nothing. For each run, it checks every copy, character, process, lease and lock the run never journalled as retired or released.

| Item | Still counts when |
|---|---|
| A process | Its ID, start time and command line all match the journal |
| A launch journalled as intended only | Its pid file names a running process with that identity and command line |
| A server launch with no pid file | Its launcher's ownership file names a launcher or game that still runs |
| A lock | Its journalled claimant still holds it |

A reused process ID is never the run's. Each run that left something is printed with its state ([the states](../packages/Valheim.Testing.NativeSmoke.md#env)). Copies made before runs were journalled are listed as `UNJOURNALLED`.

## env recover

`valheim-test env recover --run ID` clears what that one run left, exactly as `env status` judged it; `env status` is its dry run. It refuses a `LIVE`, `UNKNOWN` or `UNRECOVERABLE` run. Otherwise it works in phases:

1. Stops each process of the run whose ID, start time and command line still match. It asks the process to quit first, with the quit budget its own stop had, and kills it only after that. A Windows server's launcher tree and start task go too.
2. On each host where no process is left, under that host's lock, it retires the characters and removes the profiles and unfinished world ships. It keeps the game's logs from each profile in `recovered-<actor>` in the run's folder first. A world the run placed in a client's `worlds_local` is moved out to the run's folder, only when its metadata states the journalled world UID.
3. Releases the run's Steam leases, only when no process of the run is left on any host and every host was read.
4. Releases each host lock once nothing of the run is left on that host.

Each effect is journalled under the actor `recovery`. A step that fails keeps what depends on it for the next `env recover`. The exit code is 0 when nothing of the run is left, and 3 otherwise.

| Command | Adds |
|---|---|
| `env recover --run ID --launch-gone` | Settles an `UNRECOVERABLE` run whose only open items are launches you confirmed are gone; journals `launch-abandoned` |
| `env teardown --run ID` | As `recover`, and also removes what the run kept on purpose |
| `env teardown --copy PATH` | Removes one unjournalled copy, after keeping its changes in `<copy>-changes` |
| `env teardown --run ID --machine-gone` | Releases the leases of a run whose machine is gone for good, and journals its end |

`env teardown --copy` refuses a path that is not a toolkit copy, a copy a running process uses, and a copy an open run's journal still names. Nothing is ever removed because of its age.

## From code

`valheim-test env status`, `env recover` and `env teardown` call `EnvironmentRuns.WriteRunStatusAsync`, `RecoverRunAsync`, `TeardownCopyAsync` and `ReleaseLeasesOfGoneRunAsync` (for `--machine-gone`). `OwnedCopies.Find` lists the owned copies under a folder, and `OwnedCopies.Remove` retires one.

## How a host reports its processes

Windows lists `Win32_Process` across sessions, macOS runs one `ps -axww` listing, and Linux uses `ps` with `/proc/<pid>/cmdline`. A game image is busy even when its command line is hidden. A listing that fails, a needed command line that cannot be read, or a Linux check inside a container or under a `/proc` that hides other users is refused.

## Limits

- A run killed outright leaves no end in its journal until `env recover` proves the runner gone.
- `--machine-gone` is your confirmation that no client of that run still runs; a client that does is not told.
- A lease store on a network file system is untested. It needs hard links under bash, or an atomic no-replace rename under pwsh.
