#!/usr/bin/env bash
# sample-value.sh: sample one static member of the game or of a mod at a fixed
# interval and write the time series as CSV on stdout.
#
# Usage:
#   sample-value.sh <[Namespace.]Type.Member> <count> <interval-seconds> [arg ...] > samples.csv
#
#   sample-value.sh EnvMan.IsDay 10 5 > day.csv
#   sample-value.sh MyMod.Diagnostics.QueueLength 120 0.5 > queue.csv
#   sample-value.sh Utils.DistanceXZ 3 1 0,0,0 3,100,4
#
# Each sample is one `cli_call <member> [arg ...]` (see ValheimCLI's
# docs/cli-call.md; the member comes from its Reflection pack).
# Columns: sample,utc,value,error
#   value  the VALUE text exactly as cli_call prints it (a string keeps its
#          quotes, a vector its commas; the field is CSV-quoted), or the item
#          count when the member returns a collection
#   error  the first line of the reply when the call failed
# A failed first sample stops the script with the reply on stderr (the member
# or its arguments are wrong). A later failure is recorded and sampling goes on.
#
# The interval is the pause between calls; each call adds a few milliseconds.
# The pause sets the sampling rate of a time series. It is not a wait for a
# condition: for that, use an async command or `valheim-cli wait --for`.
#
# cli_call is cheat-gated: run `valheim-cli devcommands` in the world first.
#
# Environment:
#   VALHEIM_CLI       path to the valheim-cli executable (default: valheim-cli on PATH)
#   VALHEIM_CLI_PORT  the game's CLI port (default: 5555)
# A remote game works through an SSH tunnel to its CLI port:
#   ssh -N -L 5555:127.0.0.1:5555 user@game-host
#
# Exit code: 0 all samples written (a later failed sample is a row, not an
# exit code); 1 the first sample failed; 3 valheim-cli not found; 4 usage.
#
# sample-value.ps1 is its PowerShell twin for Windows. Moved from ValheimCLI
# (commit d112140) to ValheimTesting on 30 Sep 2026; valheim-cli itself comes
# from a ValheimCLI release or build, see tools/dev-loop/README.md.
set -euo pipefail

if [[ $# -lt 3 ]]; then
    echo "usage: $0 <[Namespace.]Type.Member> <count> <interval-seconds> [arg ...]" >&2
    exit 4
fi
member=$1
count=$2
interval=$3
shift 3

if ! [[ $count =~ ^[1-9][0-9]*$ ]]; then
    echo "count must be a whole number of samples, got '$count'" >&2
    exit 4
fi
if ! [[ $interval =~ ^[0-9]+([.][0-9]+)?$ ]]; then
    echo "interval must be a number of seconds, got '$interval'" >&2
    exit 4
fi

cli=${VALHEIM_CLI:-valheim-cli}
port=${VALHEIM_CLI_PORT:-5555}
if ! command -v "$cli" >/dev/null 2>&1; then
    echo "valheim-cli not found: '$cli' (set VALHEIM_CLI to its path)" >&2
    exit 3
fi

# Milliseconds where date knows %N (nanoseconds); whole seconds where it does not.
if [[ $(date +%N) =~ ^[0-9]{9}$ ]]; then
    fractional=1
else
    fractional=0
fi
utc_now() {
    if [[ $fractional -eq 1 ]]; then
        local now
        now=$(date -u '+%Y-%m-%dT%H:%M:%S.%N')
        printf '%sZ' "${now:0:23}"
    else
        date -u '+%Y-%m-%dT%H:%M:%SZ'
    fi
}

csv_field() {
    local text=${1//\"/\"\"}
    printf '"%s"' "$text"
}

echo "sample,utc,value,error"
for ((i = 1; i <= count; i++)); do
    utc=$(utc_now)
    status=0
    reply=$("$cli" --port "$port" cli_call "$member" "$@" 2>&1) || status=$?
    value=""
    error=""
    if [[ $status -eq 0 ]]; then
        while IFS= read -r line; do
            case $line in
                "VALUE "*) value=${line#VALUE } ;;
                "OK: CALL "*)
                    if [[ -z $value && $line =~ \ items=([0-9?]+) ]]; then
                        value=${BASH_REMATCH[1]}
                    fi
                    ;;
            esac
        done <<< "$reply"
    else
        if [[ $i -eq 1 ]]; then
            echo "first sample failed (valheim-cli exit $status):" >&2
            echo "$reply" >&2
            exit 1
        fi
        error=${reply%%$'\n'*}
    fi
    printf '%d,%s,%s,%s\n' "$i" "$utc" "$(csv_field "$value")" "$(csv_field "$error")"
    if [[ $i -lt $count ]]; then
        sleep "$interval"
    fi
done
