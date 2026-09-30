# Linux game client host

A container image for running the **Valheim game client** on a Linux machine with an NVIDIA GPU, so native client checks (ValheimCLI, [ClientSurfaceCheck](../../examples/ClientSurfaceCheck), [PaintCheck](../../examples/PaintCheck)) can run on rented or remote hardware. It is the client-side counterpart of [`docker/linux-server`](../linux-server/README.md).

It contains no credentials and no game files, so this repository's workflow publishes it to GHCR as `valheim-linux-client` (`ghcr.io/<owner>/valheim-linux-client`, see the repository's Packages). You bring your own Steam account (which must own Valheim), approve its logins by QR code, and the game is downloaded when the container runs.

## What is inside

- Ubuntu 22.04 (x86-64), Xorg, Mesa and 32-bit libraries, the Steam client with its runtime libraries, and [DepotDownloader](https://github.com/SteamRE/DepotDownloader) 3.4.0.
- Helpers in `/usr/local/bin`:

| Helper | Does |
|---|---|
| `vt-start-x` | Starts headless Xorg `:0` on the NVIDIA GPU: extracts the X driver matching the host's driver, installs NVIDIA's Vulkan manifests when the container runtime did not, and fakes a connected 1920x1080 monitor (Steam's UI will not open a window on a screen without an output). |
| `vt-qr-url <log>` | Decodes the login QR code DepotDownloader prints as text into its `https://s.team/...` link. |
| `vt-steam` | Starts the Steam client (UI rendered on the CPU). Its first login window shows a QR code. |
| `vt-screenshot [png]` | Screenshot of `:0`. With `zbarimg` it also reads the Steam client's login QR code. |
| `vt-stage-character [--replace] <name> < file.fch` | Stages a local character (never Steam Cloud). Publishes only complete, non-empty data and keeps an existing character of that name unless `--replace` is given. |

| `vt-launch-valheim [--vanilla] [--env NAME]... [args]` | Launches the client with BepInEx (the same environment as `ClientLaunch` builds for Linux) and refuses when BepInEx is missing; only `--vanilla` launches without it. Waits for Steam and for the main menu, refuses a second client, retries early start-up failures twice after stopping only the failed attempt. |
| `vt-stop-game [seconds]` | Stops the client `vt-launch-valheim` started, identified by PID and start time, and only that process. |
| `vt-pids <name>`, `vt-stop-all <name>` | Find, or stop for container-wide cleanup, every process of a program name (never by command-line pattern). |

For a character prepared with FullLifecycle's preview `prepare-character` command, use a **fresh filename** and set `client.character` to that filename, not the character's display name. An owned FullLifecycle run stages and removes its pinned copy; `vt-stage-character` is for other manual workflows. Neither stage operation proves that the game accepted the point: the client support observation must confirm arrival. This path passed a bounded [Windows native join check](https://github.com/tvongaza/ValheimTesting/pull/94#issuecomment-5912134571); the Linux client image's prepared-start path has not been checked natively.

- A user `steam` (Steam refuses to run as root) whose Valheim settings skip the intro cinematic.

`tests/helpers.sh` tests these helpers inside the image with an inert stand-in for the game (no download, no login); the workflow runs it on every change.

## Host requirements

- **A Linux VM (or machine) you control, with an NVIDIA GPU and its driver**, Docker, and the NVIDIA container toolkit. Tested on VMs with a Quadro P4000 and an RTX 2060 SUPER (drivers 535.x); Vulkan and OpenGL both work. If you have no such machine, rented GPU VMs are enough: we tested on cheap [Vast.ai](https://vast.ai) spot VM instances, which cost a few cents an hour at the time. Choose a VM instance type rather than a plain Docker instance (see the next point); prices and offers change, and a spot instance can be taken back mid-run.
- **Relaxed container security options.** Steam needs unprivileged user namespaces, and its sandbox mounts `/proc`, which Docker's default seccomp and AppArmor profiles and masked paths block. Plain container hosts that do not let you set these (typical rented "Docker instances") cannot run the Steam client; use a VM there.
- **Host stability during a run.** A systemd reload on the host (for example from unattended upgrades) makes containers that received the GPU through `--gpus` lose it (`Failed to initialize NVML: Unknown Error`; the game then fails in GLX setup). `host/vm-bootstrap.sh` stops unattended upgrades, switches Docker to the `cgroupfs` cgroup driver and passes every `/dev/nvidia*` device explicitly.

## Running it

On a fresh Ubuntu VM with an NVIDIA GPU:

```sh
bash host/vm-bootstrap.sh ghcr.io/<owner>/valheim-linux-client:latest
```

It installs the NVIDIA container toolkit, starts the container `vt` with the GPU and the options above, checks user namespaces and the GPU inside the container, and starts Xorg.

Then, inside the container (`docker exec vt ...`):

1. **Download the client.** As `steam`: `DepotDownloader -app 892970 -os linux -dir /home/steam/valheim -qr -remember-password`. It prints a login QR code; `vt-qr-url` turns it into a link that any Steam authenticator can approve (the Steam Mobile app, or for example `steamguard qr-login --url <link>` from steamguard-cli on a machine you trust).
2. **Add BepInEx and your plugins** to `/home/steam/valheim` (BepInExPack_Valheim; the Linux client needs its `doorstop_libs`).
3. **Log in the Steam client.** `vt-steam`, then read the login QR from a screenshot (`vt-screenshot /tmp/s.png && zbarimg --raw /tmp/s.png`) and approve it the same way.
4. **Stage a character** with `vt-stage-character` if your test joins a world.
5. **Launch** with `vt-launch-valheim` and drive the client through ValheimCLI (keep its port on loopback and reach it through SSH port forwarding).

The client runs on Vulkan. A dedicated server can run in the same container or next to it ([`docker/linux-server`](../linux-server/README.md), `ServerLaunch`).

## Security

- The image never contains a password, an authenticator secret or game files. Logins are approved from outside; the host only receives a Steam session.
- Anyone who controls the host can read the container, including that session. Use a Steam account you can afford to expose (not your main account), prefer verified hosts, and destroy rented machines after each run.
- Do not publish an image or a container export after the game has been downloaded into it.
