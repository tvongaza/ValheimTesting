#!/bin/bash
# Prepare a fresh Ubuntu VM with an NVIDIA GPU (for example a rented cloud VM) and start the client container `vt`.
#   bash vm-bootstrap.sh <image>        e.g. ghcr.io/<owner>/valheim-linux-client:latest
# Installs the NVIDIA container toolkit, then runs the image with the GPU and the security options Steam needs
# (user namespaces and its /proc-mounting sandbox), with --init so exited processes are reaped, and starts headless
# Xorg. Idempotent.
set -euo pipefail
IMAGE="${1:?usage: vm-bootstrap.sh <image>}"
export DEBIAN_FRONTEND=noninteractive
# A throwaway VM must not change under a run: package upgrades reload systemd, and a reload makes containers that got
# the GPU through --gpus lose it ("Failed to initialize NVML: Unknown Error"; games then fail in GLX setup).
systemctl stop unattended-upgrades apt-daily.timer apt-daily-upgrade.timer apt-daily.service apt-daily-upgrade.service > /dev/null 2>&1 || true
if ! command -v nvidia-ctk >/dev/null; then
    curl -fsSL https://nvidia.github.io/libnvidia-container/gpgkey | gpg --dearmor --yes -o /usr/share/keyrings/nvidia-container-toolkit-keyring.gpg
    curl -fsSL https://nvidia.github.io/libnvidia-container/stable/deb/nvidia-container-toolkit.list \
      | sed 's#deb https://#deb [signed-by=/usr/share/keyrings/nvidia-container-toolkit-keyring.gpg] https://#g' \
      > /etc/apt/sources.list.d/nvidia-container-toolkit.list
    apt-get update -qq && apt-get install -y -qq nvidia-container-toolkit > /dev/null
    nvidia-ctk runtime configure --runtime=docker > /dev/null 2>&1
    # cgroupfs instead of systemd's cgroup driver: device permissions then survive a systemd reload (NVIDIA's workaround).
    python3 -c 'import json; p="/etc/docker/daemon.json"; d=json.load(open(p)); d["exec-opts"]=["native.cgroupdriver=cgroupfs"]; json.dump(d, open(p,"w"), indent=2)'
    systemctl restart docker
fi
docker pull -q "$IMAGE" > /dev/null
if ! docker ps --format '{{.Names}}' | grep -qx vt; then
    docker rm -f vt > /dev/null 2>&1 || true
    # Every NVIDIA device node explicitly as well: explicit device rules are not dropped by a systemd reload.
    DEV=(); for d in /dev/nvidia*; do [ -c "$d" ] && DEV+=(--device "$d"); done
    docker run -d --init --name vt --gpus all "${DEV[@]}" \
      --security-opt seccomp=unconfined --security-opt apparmor=unconfined --security-opt systempaths=unconfined \
      -e NVIDIA_DRIVER_CAPABILITIES=all --shm-size=2g "$IMAGE" sleep infinity > /dev/null
fi
docker exec vt bash -c 'runuser -u steam -- unshare -Ur true && echo "vm-bootstrap: user namespaces OK"'
docker exec vt vt-start-x
docker exec vt nvidia-smi --query-gpu=name --format=csv,noheader > /dev/null && echo "vm-bootstrap: GPU visible in the container ($(docker info -f "{{.CgroupDriver}}") cgroups, ${#DEV[@]} explicit devices)"
