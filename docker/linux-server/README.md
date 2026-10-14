# Linux dedicated-server image

Build a private Ubuntu 24.04 image that runs the Linux Valheim dedicated server with BepInEx, and run your checks inside it.

Linux hosts run the dedicated server only. There is no Linux game client. For platform support, see [Platforms](../../docs/reference/platforms.md#linux-and-containers). For game hosts and environment inventories, see [Valheim.Testing.GameSessions](../../docs/packages/Valheim.Testing.GameSessions.md).

| In the image | Where |
|---|---|
| Valheim dedicated server (Steam app 896660), from anonymous SteamCMD | `/opt/valheim/server` |
| The server's Steam build ID | `/opt/valheim/server-buildid.txt` |
| BepInExPack_Valheim at the `loader-dependency.json` pin | Extracted over `/opt/valheim/server` |
| .NET 10 SDK | `/opt/dotnet` |

The image also has git, for `scripts/bootstrap-cli.cs`, and a non-root `valheim` user that owns `/opt/valheim`. You need no Steam, game or registry credentials.

## Keep the image private

The image and every layer, export or tarball of it contain Valheim's game files, so never push, attach or publish them. Copy logs out of a container, never `/opt/valheim`.

## Build

You need BuildKit (Docker 23 or later). Run these commands from the repository root:

```sh
dotnet run scripts/bootstrap-cli.cs
bash docker/linux-server/build.sh -t valheimtesting-linux-server:local
```

The first command fetches the pinned BepInExPack to `.packages/bepinexpack-valheim.zip` and checks its SHA-256. The second builds `valheimtesting-linux-server:local`. On Windows, run `build.sh` in Git Bash or WSL.

`build.sh` does the following:

- It passes the zip as the build context `pinned`. It passes the SHA-256 from [`loader-dependency.json`](../../loader-dependency.json) as `BEPINEX_PACK_SHA256`. The Dockerfile checks that hash again before it extracts the pack.
- It passes every other argument to `docker build`.
- If the zip is missing, it stops and tells you to run `scripts/bootstrap-cli.cs` first.

A plain `docker build` of the Dockerfile fails with "pull access denied ... pinned".

The image is x86-64 only (`FROM --platform=linux/amd64`), because the server, SteamCMD and Doorstop have no arm64 builds. No repository source enters the image. Each build downloads the current public server build, so record `server-buildid.txt` with every result. SteamCMD and the .NET SDK are not pinned to exact builds.

### Plain Ubuntu base mirror

The base image is the unmodified Ubuntu 24.04 amd64 image. It is mirrored by digest at `ghcr.io/tvongaza/valheimtesting-ubuntu-base`, with no Valheim, BepInEx or credentials. [`ubuntu-base.json`](ubuntu-base.json) records the upstream manifest digest, its compressed layer size and the GHCR package.

The native workflows pull the base only from GHCR, through `pull-ubuntu-base.sh`. If the base is unavailable, they fail with a named error. They never fall back to ECR Public or Docker Hub.

To refresh the mirror:

1. Create a branch named `mirror/ubuntu-base-YYYYMMDD`. Inspect `public.ecr.aws/ubuntu/ubuntu:24.04` and select its **linux/amd64 image manifest** digest, not the multi-platform index digest. Note that manifest's compressed layer size.
2. Update `ubuntu-base.json` and the Dockerfile's GHCR digest together. `check-ubuntu-base.sh` refuses a mismatch, and the PR CI runs it.
3. Push the branch. [Mirror plain Ubuntu base](../../.github/workflows/mirror-ubuntu-base.yml) copies the pinned manifest to a digest-specific GHCR tag and confirms its digest.
4. Review the workflow's source, digest and size summary. Before you merge, run [Native Linux server smoke](../../.github/workflows/native-linux.yml) on the branch. If GHCR cannot serve the package to native CI, keep the PR open and repair access.

## Run a check inside the container

[LinuxServerSmoke](smoke/README.md) is the smallest check. It boots the server twice with BepInEx, on a new world and then on its save, and stops it cleanly each time.

```sh
docker run --rm -it -v "$PWD:/src:ro" valheimtesting-linux-server:local bash
# Inside the container, work on a writable copy of the repository:
cp -r /src ~/repo && cd ~/repo
dotnet run --project docker/linux-server/smoke -c Release -- /opt/valheim/server ~/out
```

The smoke writes its logs and report to `~/out`.

<!-- TODO(docs): capture the smoke's console output -->

Use a fresh container for every run, or give each run its own copy of the server directory. BepInEx writes its log and configuration into `/opt/valheim/server`. The smoke refuses a runtime that already has a BepInEx log.

Your own runner is an ordinary .NET program. It builds the launch with `GameLaunch.ForServer` and owns the process through [`OwnedServerSession`](../../docs/reference/game-server-session.md):

```csharp
// runtime: a disposable copy of /opt/valheim/server with your plugins added; world: its -savedir.
var start = GameLaunch.ForServer(runtime,
    ["-batchmode", "-nographics", "-name", name, "-world", world, "-password", password, "-public", "0", "-savedir", saveDir, "-logFile", unityLog],
    new Dictionary<string, string> { ["MY_TEST_SESSION_TOKEN"] = token }).ToStartInfo();
var process = new DirectServerProcess(start, logPrefix, Path.Combine(runtime, "BepInEx", "LogOutput.log"), unityLog);
```

`GameLaunch.ForServer` detects Linux from `valheim_server.x86_64`. It starts the server executable directly, not the pack's shell script. For what it checks, sets and refuses, see [Dedicated server launch](../../docs/reference/game-launch.md#dedicated-server-launch).

## Apple Silicon (experimental)

This path is experimental and unverified. On an Apple Silicon Mac, Docker runs arm64 Linux, so this image runs only under x86-64 emulation.

`GameLaunch.ForServer` refuses to launch the Linux server on a macOS host. Inside the container it sees Linux, so runners work unchanged. To run the native macOS server instead, see [macOS dedicated server](../../docs/reference/game-launch.md#macos-dedicated-server).

```sh
bash docker/linux-server/build.sh --platform linux/amd64 -t valheimtesting-linux-server:local
docker run --platform linux/amd64 --rm -it -v "$PWD:/src:ro" valheimtesting-linux-server:local bash
```

Expect the following under emulation:

- **SteamCMD may fail.** SteamCMD is 32-bit x86, which Rosetta does not translate, so it runs under QEMU. It may hang or fail, and the build stops after three failed install attempts.
- **Runs are slower.** Everything runs several times slower. Raise the smoke's deadline (up to 1800 s) rather than trusting the default.
- **Rosetta helps.** In Docker Desktop, **Use Rosetta for x86_64/amd64 emulation on Apple Silicon** usually speeds up the 64-bit parts.
- **A pass proves less.** A pass under emulation is not evidence about native performance or timing.

On a Mac, run the [native macOS dedicated server](../../docs/reference/game-launch.md#macos-dedicated-server), a game client, or tests that talk to a Windows or Linux server on another machine.

## Scheduled checks in CI

[`native-server-checks.yml`](../../.github/workflows/native-server-checks.yml) (*Scheduled native server checks*) builds the image on a GitHub-hosted runner. It runs two checks in fresh containers:

1. **[LinuxServerSmoke](smoke/README.md).** The server starts, loads BepInEx and creates a new world.
2. **The server half of the [native acceptance suite](../../tests/Valheim.Testing.NativeAcceptance/README.md).** The suite's mod marks a dry site and refuses a wet one. It then confirms a save, restarts the server and finds the marker again.

| Trigger | When |
|---|---|
| Schedule | Every night at 03:23 UTC, on the default branch |
| [Native server build watch](../../.github/workflows/native-server-watch.yml) | Every four hours, if Steam's public build id has not passed yet |
| By hand | `gh workflow run native-server-checks.yml --ref <branch>` |

A build counts as passed only when its image build, smoke, example, evidence collection and upload all pass. After a failure, the watch starts the checks again every four hours. It stops when they pass or a new build appears. If the watch cannot read the build id, it fails. To test the watch on another branch, run `gh workflow run native-server-watch.yml --ref <branch>` with a `cache_scope`.

**Results.** The `native-server-checks` artifact is kept for 14 days. It holds `result.json`, `junit.xml`, the server and BepInEx logs, the recorded commands and `server-buildid.txt`. A failed scheduled run, or a failed run the watch started on the default branch, opens or updates the issue *Scheduled native server checks are failing* (labels `ci`, `native`). Close that issue once the checks pass. A manual run prints the issue instead, unless it runs on the default branch with `tracking_issue: open` and no negative control or `cache_scope`.

**Negative controls** are expected to fail, and they never post an issue:

- `negative_control: plugin-pin` pins a wrong MD5 for the mod, so the run refuses the server.
- `bepinex-hash` uses a wrong BepInExPack hash, so the build stops.

**Limits.** These checks use no game client and no Steam login. They say nothing about joining, crossplay or the Windows server. They test the current public server build with the pinned BepInExPack. A pass does not mean every mod or world works.

**Game files.** The image is built with `--no-cache`, and it is never pushed, saved or cached. The upload accepts only `.json`, `.jsonl`, `.xml`, `.log`, `.absent` and `server-buildid.txt` files, up to 20 MB each. The logs can contain the runner's network details.

## Networking

By default, the server listens on UDP 2456-2458 inside the container. The examples publish no ports, so nothing outside the container can join. Keep `-public 0` so the server is never listed. A game client in a container is not supported.
