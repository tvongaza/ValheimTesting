#!/usr/bin/env bash
# Investigation instrumentation for #736 (the macOS test job losing its runner); remove it when that issue closes.
# Appends a short sample of the machine to $1 every 60 s, at most 45 times: the runner agent's and the test hosts' processes
# with their process groups, the largest processes by memory, disk, swap and memory, and the last lines of validate.cs's
# transcript ($2), which name the command that was running. The test job starts it in the background, stops it and uploads
# the file with `if: always()`. A lost runner takes the file with it; validate.cs's heartbeat lines in the job log are what
# reaches GitHub while the job runs (VT_VALIDATE_HEARTBEAT_SECONDS).
set -u
out="$1"
transcript="$2"
mkdir -p "$(dirname "$out")"
for _ in $(seq 1 45); do
  {
    echo "=== $(date -u '+%Y-%m-%dT%H:%M:%SZ') processes: $(ps -ax | wc -l | tr -d ' ')"
    echo "-- runner agent, test hosts and validation (pid ppid pgid rss etime)"
    ps -axo pid,ppid,pgid,rss,etime,command | grep -E 'Runner\.(Worker|Listener)|testhost|validate\.cs|dotnet test' | grep -v grep | cut -c1-400
    echo "-- largest by memory"
    ps -axo pid,ppid,pgid,rss,pcpu,etime,command -m | head -n 13 | cut -c1-260
    echo "-- disk"
    df -h / "${RUNNER_TEMP:-/tmp}" "${GITHUB_WORKSPACE:-.}" 2>&1 | awk '!seen[$0]++'
    echo "-- swap and memory"
    sysctl -n vm.swapusage 2>&1
    echo "free memory level: $(sysctl -n kern.memorystatus_level 2>&1)%"
    vm_stat 2>&1 | head -n 6
    echo "-- validate transcript tail"
    tail -n 3 "$transcript" 2>&1 | cut -c1-300
  } >> "$out" 2>&1
  sleep 60
done
