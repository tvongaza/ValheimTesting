# Profiles and hosts

How can a test run a modded Valheim on your own Steam install without changing it, and on machines other than the one you type on?

## A profile, not a copy

A native run never installs its mods into your game, and it never copies the game either. It builds a **disposable loader profile** for each game process it owns: the game's own files, plus the loader, ValheimCLI and the plugins this run chose.

- On Windows, the profile's launch folder hard-links the install's launch files, so it costs almost no disk. A hard link cannot cross volumes, so the profile must sit on the install's NTFS volume.
- On Linux and macOS, the install's own executable starts with Doorstop pointed at the profile.

Your install is only read. The run checks it before staging and compares it again after the run; a file that changed fails the run. The profile goes once its process has stopped, and what the run wrote into it is kept as evidence.

The escape hatch is `--in-place`: an owned client runs its install exactly as it is, with nothing staged. Use it when you manage the client's plugins yourself.

## Hosts

A **host** is a machine a run works on. Every host runs the same small scripts in its OS's one shell (`pwsh` on Windows, `bash` elsewhere), so a run behaves the same everywhere.

| Host | Reached | Runs |
|---|---|---|
| This machine | `local` | Server and client |
| Another Windows or Mac | `ssh` | Server; a client in its signed-in desktop session |
| A Linux machine or container | `ssh`, `local` or `container` | The dedicated server only |

You describe your hosts once in an **inventory file**: the machines, and in preference order the installs on them. With no file, the inventory is this machine. A run picks an install for each actor from it.

## Claims and the journal

Several runs can share machines, so every run claims what it uses before it touches it:

- **The host lock.** One run at a time works on a host. Another run is refused at once, never queued.
- **Steam-account leases.** Steam allows one session per account. A run leases the account signed in on each client host, so two runs never play on one account.
- **The run journal.** Before a run makes a profile, a world copy, a character or a process, it writes a line saying so on that host. It writes another once the thing is gone.

When a run dies half-way, the journal says exactly what it left. `valheim-test env status` reads every journal and changes nothing. `valheim-test env recover --run ID` removes only what it can prove that run made and has stopped using. Nothing is ever removed because of its age.

## In practice

```sh
valheim-test env status
```

On a clean machine it prints only a count, and exits 0:

```text
N runs ended and left nothing. Nothing was changed.
```

If a run left something, the output names the run, its state and the command that clears it.

## Related

- [Disposable profiles](../reference/sessions-profiles.md): what a profile holds and where it lives.
- [Game hosts](../reference/sessions-hosts.md) and [inventory file](../reference/sessions-inventory.md).
- [Locks, leases and recovery](../reference/sessions-leases-and-recovery.md).
- [Run on another machine](../guides/run-on-another-machine.md).
