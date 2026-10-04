#!/usr/bin/env bash
# The game-hosts job's throwaway hosts, without a game. `start` runs an sshd on 127.0.0.1:2222 that accepts only a key
# generated for this run, and an Xvfb display on :99 for the interactive client checks, and exports their settings
# through $GITHUB_ENV; `stop` removes both. The keys live in $RUNNER_TEMP. The lease contention checks open ten ssh
# sessions at once, beyond sshd's default MaxStartups of 10 unauthenticated connections. The client key and
# known_hosts sit in a directory with a space in its name, so every connection passes quoted ssh options.
set -euo pipefail
d="$RUNNER_TEMP/sshd"
k="$RUNNER_TEMP/vt client key"

case "${1:-}" in
  start)
    mkdir -p "$d"
    if [ ! -x /usr/sbin/sshd ]; then sudo apt-get update -q && sudo apt-get install -y -q openssh-server; fi
    ssh-keygen -q -t ed25519 -N '' -C vt-host -f "$d/host_key"
    mkdir -p "$k"
    ssh-keygen -q -t ed25519 -N '' -C vt-client -f "$k/client_key"
    cp "$k/client_key.pub" "$d/authorized_keys"
    printf '%s\n' "Port 2222" "ListenAddress 127.0.0.1" "HostKey $d/host_key" "PidFile $d/sshd.pid" \
      "AuthorizedKeysFile $d/authorized_keys" "PasswordAuthentication no" "KbdInteractiveAuthentication no" \
      "PubkeyAuthentication yes" "UsePAM yes" "StrictModes no" "AllowTcpForwarding local" "AllowAgentForwarding no" \
      "X11Forwarding no" "MaxStartups 100" "Subsystem sftp internal-sftp" > "$d/sshd_config"
    sudo mkdir -p /run/sshd
    sudo /usr/sbin/sshd -f "$d/sshd_config" -E "$d/sshd.log"
    echo "[127.0.0.1]:2222 $(cut -d' ' -f1,2 "$d/host_key.pub")" > "$k/known_hosts"
    echo "VALHEIM_TESTING_SSH_DESTINATION=$(id -un)@127.0.0.1" >> "$GITHUB_ENV"
    {
      echo "VALHEIM_TESTING_SSH_OPTIONS<<VT_OPTIONS"
      echo "IdentityFile=$k/client_key"
      echo "IdentitiesOnly=yes"
      echo "UserKnownHostsFile=$k/known_hosts"
      echo "StrictHostKeyChecking=yes"
      echo "VT_OPTIONS"
    } >> "$GITHUB_ENV"
    # ssh reads a -o value like a config line, so a path with a space is quoted inside the argument.
    ssh -o BatchMode=yes -o "IdentityFile=\"$k/client_key\"" -o IdentitiesOnly=yes -o "UserKnownHostsFile=\"$k/known_hosts\"" \
      -o StrictHostKeyChecking=yes -p 2222 "$(id -un)@127.0.0.1" 'echo "ssh to localhost works; pwsh: $(command -v pwsh)"'

    if ! command -v Xvfb > /dev/null; then sudo apt-get update -q && sudo apt-get install -y -q xvfb; fi
    Xvfb :99 -nolisten tcp > "$RUNNER_TEMP/xvfb.log" 2>&1 &
    echo $! > "$RUNNER_TEMP/xvfb.pid"
    for _ in $(seq 1 100); do [ -S /tmp/.X11-unix/X99 ] && break; sleep 0.1; done
    [ -S /tmp/.X11-unix/X99 ] || { cat "$RUNNER_TEMP/xvfb.log"; exit 1; }
    echo "VALHEIM_TESTING_DISPLAY=:99" >> "$GITHUB_ENV"
    ;;
  stop)
    if [ -f "$RUNNER_TEMP/xvfb.pid" ]; then kill "$(cat "$RUNNER_TEMP/xvfb.pid")" || true; fi
    if [ -f "$d/sshd.pid" ]; then sudo kill "$(cat "$d/sshd.pid")" || true; fi
    cat "$d/sshd.log" 2>/dev/null || true
    rm -rf "$d" "$k"
    ;;
  *) echo "usage: game-hosts.sh start|stop" >&2; exit 2 ;;
esac
