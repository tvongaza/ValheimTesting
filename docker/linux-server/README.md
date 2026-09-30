# Linux dedicated-server image

A local Ubuntu 24.04 image for owned native checks against the **Linux** Valheim dedicated server. It contains:

- the Valheim dedicated server (Steam app 896660) in `/opt/valheim/server`, installed with anonymous SteamCMD while the image is built; its Steam build ID is in `/opt/valheim/server-buildid.txt`;
- [BepInExPack_Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) 5.4.2350 extracted over that directory, after its SHA-256 is verified;
- the .NET 10 SDK (Microsoft's `dotnet-install.sh`, channel `10.0`) in `/opt/dotnet`;
- a non-root `valheim` user that owns `/opt/valheim`.
- git, for `scripts/bootstrap-cli.cs`.

No credentials are needed or accepted. SteamCMD logs in anonymously; the dedicated server is free.

**Verified** on a Linux x86-64 Docker host on 28 September 2026 with dedicated-server build 25527701: the image built, [LinuxServerSmoke](../../examples/LinuxServerSmoke/README.md) passed all five steps (BepInEx chainloader after about 5 s, the new world loaded after about 51 s), and `scripts/validate.cs` passed inside the container. Only the Apple Silicon (emulated) path below remains experimental.

## Keep the image private

The image and every layer, export or saved tarball of it contain Valheim's game files. **Never push it to a registry, attach it to a release or publish it in any other form.** Build it where it is used, locally or in a CI job, and let it be discarded with that machine. Publish only your own reviewed logs and reports, never the server directory. The same rule applies to a container's filesystem: copy out logs, not `/opt/valheim`.

## Build

From the repository root:

```sh
docker build -t valheimtesting-linux-server:local docker/linux-server
```

The image is x86-64 only: the Dockerfile pins `FROM --platform=linux/amd64`, because the server, SteamCMD and Doorstop have no arm64 builds.

The build context is this directory only; no repository source enters the image. Each build downloads the current public dedicated-server build, so record `server-buildid.txt` with any result. The base image is pinned by tag, SteamCMD and the .NET SDK are not pinned to exact builds; the BepInEx pack is pinned by version and hash, and its launch script is checked for the loader variables `ServerLaunch` reproduces.

## Run a check inside the container

A runner inside the container is an ordinary .NET program. The [LinuxServerSmoke](../../examples/LinuxServerSmoke/README.md) example is the smallest one: it boots the server once with BepInEx and stops it.

```sh
docker run --rm -it -v "$PWD:/src:ro" valheimtesting-linux-server:local bash
# Inside the container, work on a writable copy of the repository:
cp -r /src ~/repo && cd ~/repo
dotnet run --project examples/LinuxServerSmoke -c Release -- /opt/valheim/server ~/out
```

A fresh container is a disposable runtime: BepInEx writes its log and configuration into `/opt/valheim/server`, and the smoke refuses a runtime that already has a BepInEx log. Start a new container for another run, or give each run its own copy of the server directory.

Your own runner builds the launch the same way, then owns the process through `OwnedServerSession` as on Windows:

```csharp
// runtime: a disposable copy of /opt/valheim/server with your plugins added; world: its -savedir.
var start = ServerLaunch.CreateStartInfo(runtime,
    ["-batchmode", "-nographics", "-name", name, "-world", world, "-password", password, "-public", "0", "-savedir", saveDir, "-logFile", unityLog],
    new Dictionary<string, string> { ["MY_TEST_SESSION_TOKEN"] = token });
var process = new DirectServerProcess(start, logPrefix, Path.Combine(runtime, "BepInEx", "LogOutput.log"), unityLog);
```

`ServerLaunch` detects Linux from `valheim_server.x86_64`, requires its execute bit and BepInEx's preloader, core and Doorstop library, refuses caller Doorstop variables and `--doorstop-*` arguments, and sets `DOORSTOP_ENABLED`, `DOORSTOP_TARGET_ASSEMBLY`, `LD_LIBRARY_PATH` (prepended), `LD_PRELOAD` (prepended) and `SteamAppId`. It starts the server executable directly, not the pack's shell script, so the session's PID check still holds.

## Apple Silicon (experimental)

There is no macOS dedicated server, and `ServerLaunch` refuses to launch a server on a macOS host. On an Apple Silicon Mac, Docker runs arm64 Linux, so this image only runs under x86-64 emulation. This path is **experimental and unverified**: no pass has been recorded on a Mac. Inside the container `ServerLaunch` sees a Linux host, so runners work unchanged.

```sh
docker build --platform linux/amd64 -t valheimtesting-linux-server:local docker/linux-server
docker run --platform linux/amd64 --rm -it -v "$PWD:/src:ro" valheimtesting-linux-server:local bash
```

Caveats:

- SteamCMD is a 32-bit x86 program. Rosetta does not translate 32-bit x86 code, so SteamCMD depends on Docker's QEMU emulation. It may hang or fail during the image build; a failed install stops the build after three attempts.
- Everything in the container is emulated, including the .NET SDK and the server's world generation. Expect builds and boots to be several times slower than on an x86-64 host; raise the smoke's deadline (up to 1800 s) rather than trusting the default.
- In Docker Desktop, enabling **Use Rosetta for x86_64/amd64 emulation on Apple Silicon** usually makes the 64-bit parts faster.
- A pass under emulation shows the server boots under emulation. It is not evidence about native performance or timing.

The supported Mac workflows are a Mac game client (launched through ValheimCLI, which handles macOS `Valheim.app`) and Mac-run tests or runners that talk to a Windows or Linux dedicated server on another machine. Use the container on a Mac for experiments only.

## Scheduled checks in CI

[`native-server-checks.yml`](../../.github/workflows/native-server-checks.yml) builds this image on a GitHub-hosted runner and runs two checks in fresh containers of it:

1. [LinuxServerSmoke](../../examples/LinuxServerSmoke/README.md): the server starts, loads BepInEx and creates a new world.
2. The [FullLifecycle](../../examples/FullLifecycle/README.md#the-server-half-alone) example's server half: ValheimCLI (core, Standard and WorldTools, from the commit in `cli-dependency.json`), the example mod and its adapter are built against this server's own assemblies; `prepare-server` creates a new world and picks a dry and a wet site from its generator heights; the pinned `dry-site-server` run then has the mod mark one and refuse the other, confirms a save, restarts only its server and finds the marker again.

**When.** Every night at 03:23 UTC, and every four hours a cheap job asks Steam (anonymous `app_info_print`, no download) for the public branch's build id and runs the checks only if that build has not been checked yet. Only a build whose image build, smoke and example all passed is remembered, in the Actions cache as a text file (never for a negative control): after a failure the watch checks the same build again every four hours until it passes or a new build appears; the nightly run and a manual run always check. A run can also be started by hand (**Actions > Scheduled native server checks > Run workflow**, or `gh workflow run native-server-checks.yml --ref <branch>`; `watch_only` makes it decide like the watch); only the default branch's copy runs on schedule. A run on another branch also sees the default branch's record, so to test the watch itself give the runs the same `cache_scope`, which keeps their record apart.

**Results.** The run summary shows the server's Steam build id, each check's result and failed steps, and the list and size of uploaded files. The `native-server-checks` artifact (14 days) holds `result.json`, `junit.xml`, the server and BepInEx logs and the recorded commands, and `server-buildid.txt`. A failed scheduled run opens the issue *Scheduled native server checks are failing* (labels `ci`, `native`), or comments on it while it is open; close it once the checks pass again. Only an open issue the workflow opened itself (author `github-actions[bot]`, both labels) counts: an issue with the same title opened by anyone else is ignored, and the issue jobs first run the lookup's self-test (`tracking-issue.sh --self-test`) to show that. Only that job gets `issues: write`. A manual run prints the issue it would post instead (a dry run with a read-only token) unless it runs on the default branch with `tracking_issue: open`.

**Negative controls**, by hand, are expected to fail and never post an issue: `negative_control: plugin-pin` pins a wrong MD5 for the mod, so the pinned run refuses the server before the scenario; `bepinex-hash` builds the image with a wrong BepInExPack hash, so the build stops.

**What it does not cover.** There is no game client and no Steam login: nothing about joining, what a client sees, crossplay or the Windows server. It checks the current public server build with the pinned BepInExPack; a new BepInExPack is picked up by updating the pin here. A pass shows the server path works on that build, not that every mod or world does.

**Game files.** The image is built with `--no-cache`, never pushed, saved or cached, and removed with the runner. The runtime copies, the world, the publicized assemblies and the plan (it holds the throwaway server password) stay in the containers; the upload step refuses anything but `.json`, `.jsonl`, `.xml`, `.log`, `.absent` and `server-buildid.txt` files and anything over 20 MB. No secrets are used. The logs can contain the runner's network details.

## Networking

The server listens on UDP 2456-2458 inside the container by default. The examples do not publish ports: nothing outside the container can join. Keep `-public 0` so a test server is never listed. A game client in a container is not supported.
