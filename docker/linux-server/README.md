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

`ServerLaunch` detects Linux from `valheim_server.x86_64`, requires its execute bit and the BepInEx preloader and Doorstop library, and sets `DOORSTOP_ENABLED`, `DOORSTOP_TARGET_ASSEMBLY`, `LD_LIBRARY_PATH` (prepended), `LD_PRELOAD` (prepended) and `SteamAppId`. It starts the server executable directly, not the pack's shell script, so the session's PID check still holds.

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

## Networking

The server listens on UDP 2456-2458 inside the container by default. The examples do not publish ports: nothing outside the container can join. Keep `-public 0` so a test server is never listed. A game client in a container is not supported.
