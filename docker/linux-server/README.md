# Linux dedicated-server image

A local Ubuntu 24.04 image for owned native checks against the **Linux** Valheim dedicated server. It contains:

- the Valheim dedicated server (Steam app 896660) in `/opt/valheim/server`, installed with anonymous SteamCMD while the image is built; its Steam build ID is in `/opt/valheim/server-buildid.txt`;
- [BepInExPack_Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) 5.4.2350 extracted over that directory, after its SHA-256 is verified;
- the .NET 10 SDK (Microsoft's `dotnet-install.sh`, channel `10.0`) in `/opt/dotnet`;
- a non-root `valheim` user that owns `/opt/valheim`.

No credentials are needed or accepted. SteamCMD logs in anonymously; the dedicated server is free.

## Keep the image private

The image and every layer, export or saved tarball of it contain Valheim's game files. **Never push it to a registry, attach it to a release or publish it in any other form.** Build it where it is used, locally or in a CI job, and let it be discarded with that machine. Publish only your own reviewed logs and reports, never the server directory. The same rule applies to a container's filesystem: copy out logs, not `/opt/valheim`.

## Build

From the repository root:

```sh
docker build -t valheimtesting-linux-server:local docker/linux-server
```

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

The server listens on UDP 2456-2458 inside the container by default. The examples do not publish ports: nothing outside the container can join. Keep `-public 0` so a test server is never listed. A game client in a container is not supported.
