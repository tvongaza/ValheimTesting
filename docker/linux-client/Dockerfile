# Valheim game client host for a Linux machine with an NVIDIA GPU: headless Xorg on the GPU, the Steam client,
# DepotDownloader and helpers to launch the game (with BepInEx) and drive it through ValheimCLI. See README.md.
#
# PUBLIC IMAGE: it holds no credentials and no game files. Steam logins are approved from outside by QR code (the
# Steam Mobile app or another authenticator); the game is downloaded at run time; the NVIDIA X driver matching the
# host is extracted at start. There is no SSH server: the host provides SSH, and a baked sshd would ship one set of
# host keys to every container.
#
# Steam needs unprivileged user namespaces and its sandbox mounts /proc, which Docker's defaults block. Run with
#   --security-opt seccomp=unconfined --security-opt apparmor=unconfined --security-opt systempaths=unconfined
# as host/vm-bootstrap.sh does inside a VM you control. Container hosts that fix these options cannot run Steam.
#
# x86-64 only (Steam client, Valheim for Linux); the platform is pinned like docker/linux-server.
FROM --platform=linux/amd64 ubuntu:22.04
LABEL org.opencontainers.image.source=https://github.com/tvongaza/ValheimTesting \
      org.opencontainers.image.description="Valheim game client host: headless Xorg on an NVIDIA GPU, Steam client, DepotDownloader. No credentials, no game files."
ARG DEPOTDOWNLOADER_VERSION=3.4.0
ENV DEBIAN_FRONTEND=noninteractive \
    NVIDIA_VISIBLE_DEVICES=all \
    NVIDIA_DRIVER_CAPABILITIES=all \
    DISPLAY=:0
RUN dpkg --add-architecture i386 \
 && apt-get update \
 && apt-get install -y --no-install-recommends \
      ca-certificates wget curl unzip xz-utils kmod procps less \
      xserver-xorg-core x11-utils x11-xserver-utils xdotool mesa-utils imagemagick zbar-tools \
      dbus-x11 libvulkan1 libvulkan1:i386 libgl1 libgl1:i386 libegl1 libegl1:i386 libc6:i386 \
      libgl1-mesa-dri libgl1-mesa-dri:i386 libgbm1:i386 xdg-desktop-portal xdg-desktop-portal-gtk \
      libpulse-mainloop-glib0 \
      libnss3 libgbm1 libxss1 libasound2 libpulse0 zenity xdg-utils python3 \
 && wget -q -O /tmp/steam.deb https://cdn.akamai.steamstatic.com/client/installer/steam.deb \
 && apt-get install -y --no-install-recommends /tmp/steam.deb \
 && apt-get update \
 && apt-get install -y --no-install-recommends steam-libs-amd64 steam-libs-i386 \
 && rm -f /tmp/steam.deb \
 && apt-cache policy | grep -q repo.steampowered.com/steam
# apt's lists stay: Steam's launcher (steamdeps) opens an interactive "packages cache out of date" terminal
# prompt when the Steam repository is missing from `apt-cache policy`.
RUN wget -q -O /tmp/dd.zip "https://github.com/SteamRE/DepotDownloader/releases/download/DepotDownloader_${DEPOTDOWNLOADER_VERSION}/DepotDownloader-linux-x64.zip" \
 && mkdir -p /opt/depotdownloader && unzip -q /tmp/dd.zip -d /opt/depotdownloader && chmod +x /opt/depotdownloader/DepotDownloader \
 && ln -s /opt/depotdownloader/DepotDownloader /usr/local/bin/DepotDownloader && rm /tmp/dd.zip
# Steam refuses to run as root. Valheim's settings start with the intro cinematic skipped (SkipIntroCinematic).
RUN useradd -m -u 1000 -s /bin/bash steam && mkdir -p /home/steam/valheim /home/steam/.config/unity3d/IronGate/Valheim \
 && printf '<unity_prefs version_major="1" version_minor="1">\n<pref name="SkipIntroCinematic" type="int">1</pref>\n</unity_prefs>\n' \
      > /home/steam/.config/unity3d/IronGate/Valheim/prefs \
 && chown -R steam:steam /home/steam
COPY scripts/ /usr/local/bin/
RUN chmod +x /usr/local/bin/vt-*
