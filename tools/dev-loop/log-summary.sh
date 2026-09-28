#!/usr/bin/env bash
# Count the warnings and errors in a BepInEx log, by source and by message.
#
#   log-summary.sh [log-file] [--top N] [--fail-on warning|error]
#
# A test run that "passed" while a mod logged forty warnings has not passed:
# read this summary after every run. It prints one row per level and source,
# then the most frequent messages with their numbers masked (so "zone 3,-4"
# and "zone 5,2" count as one message). BepInEx rewrites LogOutput.log at
# every game start, so the file holds exactly one run.
#
#   --top N           how many messages to list (default 15)
#   --fail-on LEVEL   exit 1 when there is at least one line at LEVEL or worse
#                     (error = Error and Fatal; warning = those plus Warning)
#
# Environment:
#   VALHEIM_PATH  game folder that contains BepInEx (default: the Steam folder for this OS)
#   VALHEIM_LOG   log file to read instead of <VALHEIM_PATH>/BepInEx/LogOutput.log
set -euo pipefail

default_game_path() {
  case "$(uname -s)" in
    Darwin) echo "$HOME/Library/Application Support/Steam/steamapps/common/Valheim" ;;
    *)      echo "$HOME/.steam/debian-installation/steamapps/common/Valheim" ;;
  esac
}

log=${VALHEIM_LOG:-${VALHEIM_PATH:-$(default_game_path)}/BepInEx/LogOutput.log}
top=15
fail_on=
while [ $# -gt 0 ]; do
  case "$1" in
    --top) top=${2:?--top needs a number}; shift 2 ;;
    --fail-on) fail_on=${2:?--fail-on needs warning or error}; shift 2 ;;
    -h|--help) sed -n '2,19p' "$0"; exit 0 ;;
    -*) echo "unknown option: $1" >&2; exit 4 ;;
    *) log=$1; shift ;;
  esac
done
case "$fail_on" in ''|warning|error) ;; *) echo "--fail-on takes warning or error" >&2; exit 4 ;; esac
[ -f "$log" ] || { echo "ERROR: no log at $log" >&2; exit 3; }

# BepInEx lines look like "[Warning  :   MyMod] message"; stack-trace lines
# that follow an exception carry no prefix and are not counted separately.
awk -v top="$top" -v fail_on="$fail_on" '
  {
    sub(/\r$/, "")
    if (match($0, /^\[(Warning|Error|Fatal) *: *[^]]*\] /) == 0) next
    head = substr($0, 2, RLENGTH - 3)
    msg = substr($0, RLENGTH + 1)
    level = head; sub(/ *:.*/, "", level)
    source = head; sub(/^[^:]*: */, "", source); sub(/ +$/, "", source)
    rows[level "\t" source]++
    total[level]++
    gsub(/-?[0-9]+(\.[0-9]+)?/, "#", msg)
    if (length(msg) > 140) msg = substr(msg, 1, 137) "..."
    msgs["[" level ": " source "] " msg]++
  }
  END {
    printf "%-8s %6s  %s\n", "LEVEL", "COUNT", "SOURCE"
    for (k in rows) { split(k, p, "\t"); printf "%-8s %6d  %s\n", p[1], rows[k], p[2] | "sort -k2,2nr" }
    close("sort -k2,2nr")
    printf "\ntotal: fatal=%d error=%d warning=%d\n", total["Fatal"], total["Error"], total["Warning"]
    if (length(msgs) > 0) {
      printf "\nmost frequent messages (numbers shown as #):\n"
      cmd = "sort -k1,1nr | head -n " top
      for (m in msgs) printf "%6dx  %s\n", msgs[m], m | cmd
      close(cmd)
    }
    bad = total["Fatal"] + total["Error"]
    if (fail_on == "warning") bad += total["Warning"]
    exit (fail_on != "" && bad > 0) ? 1 : 0
  }
' "$log"
