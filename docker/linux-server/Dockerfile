# Linux Valheim dedicated server with BepInEx and the .NET 10 SDK, for owned native checks.
#
# PRIVATE, LOCAL IMAGE. It contains the Valheim dedicated server, downloaded by SteamCMD while the image
# is built. Never push it to a registry, attach it to a release or publish a layer or export of it.
# See README.md. The build uses anonymous SteamCMD and public downloads only: no credentials.
#
# x86-64 only: the server, SteamCMD (32-bit x86) and Doorstop have no arm64 builds. The platform is pinned so
# an arm64 host (Apple Silicon) builds the amd64 image under emulation instead of a broken arm64 one; that path
# is experimental. See README.md. This digest is an exact GHCR copy of Canonical's plain Ubuntu amd64 manifest.
# Its upstream digest and deliberate refresh procedure are in ubuntu-base.json and README.md.
FROM --platform=linux/amd64 ghcr.io/tvongaza/valheimtesting-ubuntu-base@sha256:a2d43bffc46670d7108bb3c0468c67c5557c4c92e83e1e34cbff1322ad1b94dc

ARG DOTNET_CHANNEL=10.0
ARG VALHEIM_SERVER_APP=896660
# The BepInExPack is not downloaded here: build.sh passes the repository's pinned zip (.packages/bepinexpack-valheim.zip, fetched
# and checked by scripts/bootstrap-cli.cs) as the build context "pinned" and its SHA-256 from loader-dependency.json as this
# argument. It has no default: a build without it fails instead of trusting a file. Build with build.sh, not a plain `docker build`.
ARG BEPINEX_PACK_SHA256

ENV DEBIAN_FRONTEND=noninteractive \
    DOTNET_ROOT=/opt/dotnet \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

# git is for scripts/bootstrap-cli.cs, which fetches the pinned ValheimCLI commit.
# SteamCMD is 32-bit (lib32gcc-s1, lib32stdc++6). The server needs libatomic1 and PulseAudio client
# libraries. The .NET SDK needs ICU, OpenSSL and zlib.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates curl unzip git \
      lib32gcc-s1 lib32stdc++6 \
      libatomic1 libpulse0 libpulse-mainloop-glib0 \
      libicu74 libssl3t64 zlib1g tzdata \
 && rm -rf /var/lib/apt/lists/*

# Microsoft's install script, pinned to one SDK channel.
RUN curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && bash /tmp/dotnet-install.sh --channel "$DOTNET_CHANNEL" --install-dir "$DOTNET_ROOT" \
 && rm /tmp/dotnet-install.sh \
 && ln -s "$DOTNET_ROOT/dotnet" /usr/local/bin/dotnet \
 && dotnet --info

RUN useradd --create-home --shell /bin/bash --uid 10001 valheim \
 && mkdir -p /opt/steamcmd /opt/valheim/server \
 && chown -R valheim:valheim /opt/steamcmd /opt/valheim
USER valheim
WORKDIR /home/valheim

# Anonymous SteamCMD install at build time. SteamCMD updates itself on first run and sometimes needs a
# second attempt, so success is judged by the app manifest (StateFlags 4 = fully installed), not exit codes.
RUN set -eu; \
    curl -fsSL https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz | tar -xz -C /opt/steamcmd; \
    manifest=/opt/valheim/server/steamapps/appmanifest_${VALHEIM_SERVER_APP}.acf; \
    for attempt in 1 2 3; do \
      /opt/steamcmd/steamcmd.sh +force_install_dir /opt/valheim/server +login anonymous \
        +app_update "$VALHEIM_SERVER_APP" validate +quit || true; \
      if [ -f /opt/valheim/server/valheim_server.x86_64 ] && [ -f "$manifest" ] \
         && grep -Eq '"StateFlags"[[:space:]]+"4"' "$manifest"; then break; fi; \
      if [ "$attempt" = 3 ]; then echo "SteamCMD did not install app $VALHEIM_SERVER_APP" >&2; exit 1; fi; \
    done; \
    sed -n 's/.*"buildid"[[:space:]]*"\([0-9]*\)".*/\1/p' "$manifest" | head -n 1 > /opt/valheim/server-buildid.txt; \
    echo "Valheim dedicated server build $(cat /opt/valheim/server-buildid.txt)"

# BepInExPack_Valheim from the build context "pinned" (a read-only mount: no layer holds the zip), verified by SHA-256 again before
# anything is extracted. Its launch script is checked for the variables GameLaunch.ForServer reproduces, so a pack with a
# different loader contract fails here.
RUN --mount=type=bind,from=pinned,source=bepinexpack-valheim.zip,target=/tmp/bepinex.zip \
    set -eu; \
    test -n "${BEPINEX_PACK_SHA256}"; \
    echo "${BEPINEX_PACK_SHA256}  /tmp/bepinex.zip" | sha256sum -c -; \
    mkdir /tmp/bepinex; \
    unzip -q /tmp/bepinex.zip -d /tmp/bepinex; \
    script=/tmp/bepinex/BepInExPack_Valheim/start_server_bepinex.sh; \
    grep -q '^export DOORSTOP_ENABLED=1' "$script"; \
    grep -q '^export DOORSTOP_TARGET_ASSEMBLY=./BepInEx/core/BepInEx.Preloader.dll' "$script"; \
    grep -q 'LD_LIBRARY_PATH="./doorstop_libs:' "$script"; \
    grep -q 'LD_LIBRARY_PATH="./linux64:' "$script"; \
    grep -q 'LD_PRELOAD="libdoorstop_x64.so:' "$script"; \
    grep -q '^export SteamAppId=892970' "$script"; \
    cp -R /tmp/bepinex/BepInExPack_Valheim/. /opt/valheim/server/; \
    rm -rf /tmp/bepinex; \
    chmod u+x,g+x /opt/valheim/server/valheim_server.x86_64 /opt/valheim/server/doorstop_libs/libdoorstop_x64.so; \
    test -f /opt/valheim/server/BepInEx/core/BepInEx.Preloader.dll; \
    test ! -e /opt/valheim/server/BepInEx/LogOutput.log

CMD ["bash"]
