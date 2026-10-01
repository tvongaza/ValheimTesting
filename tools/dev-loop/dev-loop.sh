#!/usr/bin/env bash
# One round of a mod's edit-build-test loop: build, install, launch, run a
# test plan, and summarise the log.
#
#   dev-loop.sh <MyMod.csproj> [test-plan.yaml]
#
# smoke-plan.yaml, next to this script, is a strict plan to start from.
#
#   1. dotnet build -c Release (stops on a failed build)
#   2. with a plan: writes a temporary copy of VALHEIM_EXPECTATIONS whose pin
#      for this mod is the md5 of the DLL just built (every other line kept)
#   3. copies the built DLL (and its .pdb) into BepInEx/plugins
#   4. launches Valheim and runs the plan with valheim-cli --expect-strict
#      <derived pins> --test ... --launch (without a plan: launches and waits
#      until the console is ready)
#   5. prints log-summary.sh for the run: a plan that passed while the mod
#      logged errors is worth a look
#
# Every wait prints a heartbeat (WAIT: elapsed, state, load phase, what
# changed) every PROGRESS, so a slow load and a stuck one look different.
# A plan's waitFor step also fails early when nothing changes for STALL, or
# at once when the game sits in a state that cannot reach the target (a
# world when the plan waits for the main menu).
#
# The game must not be running: a running game keeps the old build (and on
# Windows locks the file). Quit it first, or reload without a restart via
# BepInEx ScriptEngine where your mod supports it.
#
# Exit code: the build's if it failed, otherwise the plan's (or the launch's).
#
# Environment:
#   VALHEIM_CLI       valheim-cli executable (default: valheim-cli on PATH)
#   VALHEIM_EXPECTATIONS  required pins file when running a test plan; strict mode.
#                     It must pin this mod exactly once; that pin is replaced
#                     by the fresh build's md5, never by what the game reports
#   VALHEIM_PLUGIN_KEY  the key that pins this mod in that file: its GUID, name
#                     or DLL name (default: the built DLL's name without .dll)
#   VALHEIM_CLI_PORT  valheimCLI port (default 5555)
#   VALHEIM_PATH      game folder that contains BepInEx (default: the Steam folder for this OS)
#   CONFIGURATION     build configuration (default Release)
#   STOP_AFTER=1      quit the game after a plan that passed
#   PROGRESS          heartbeat interval, e.g. 30s (default 15s; 0 disables)
#   STALL             end a plan's wait after this long without change
#                     (default 120s; 0 disables; a step's own stall: wins)
#
# Moved from ValheimCLI (commit ee4cd23) to ValheimTesting on 28 Sep 2026.
# valheim-cli itself comes from a ValheimCLI release or build; see
# tools/dev-loop/README.md.
set -euo pipefail

[ $# -ge 1 ] || { sed -n '2,6p' "$0" >&2; exit 4; }
project=$1
plan=${2:-}
here=$(cd "$(dirname "$0")" && pwd)
cli=${VALHEIM_CLI:-valheim-cli}
port=${VALHEIM_CLI_PORT:-5555}
config=${CONFIGURATION:-Release}
pins=${VALHEIM_EXPECTATIONS:-}
if [ -n "$plan" ] && { [ -z "$pins" ] || [ ! -s "$pins" ]; }; then
  echo "ERROR: set VALHEIM_EXPECTATIONS to a non-empty pins file before running a test plan" >&2
  exit 4
fi

md5_of() {
  if command -v md5sum >/dev/null 2>&1; then md5sum "$1" | awk '{print $1}'
  elif command -v md5 >/dev/null 2>&1; then md5 -q "$1"
  elif command -v openssl >/dev/null 2>&1; then openssl dgst -md5 < "$1" | awk '{print $NF}'
  else echo "ERROR: need md5sum, md5 or openssl to hash $1" >&2; return 1
  fi
}

# Print the pins file with the one line whose key is $2 (case-insensitive)
# replaced by $2=$3. Fails unless exactly one line pins that key.
derive_pins() {
  awk -v key="$2" -v md5="$3" '
    function trim(s) { sub(/^[ \t]+/, "", s); sub(/[ \t]+$/, "", s); return s }
    {
      eq = index($0, "=")
      if (eq > 0 && tolower(trim(substr($0, 1, eq - 1))) == tolower(key)) {
        print key "=" md5 "   # derived by dev-loop.sh from the build it deployed"
        replaced++
        next
      }
      print
    }
    END { if (replaced != 1) exit 3 }' "$1"
}

default_game_path() {
  case "$(uname -s)" in
    Darwin) echo "$HOME/Library/Application Support/Steam/steamapps/common/Valheim" ;;
    *)      echo "$HOME/.steam/debian-installation/steamapps/common/Valheim" ;;
  esac
}
game=${VALHEIM_PATH:-$(default_game_path)}
plugins="$game/BepInEx/plugins"
[ -d "$plugins" ] || { echo "ERROR: no BepInEx/plugins under $game (set VALHEIM_PATH)" >&2; exit 3; }

# --status exits nonzero when the plugin is unavailable, even when its
# independent local-process check found the game. Inspect that evidence
# separately; an absent/unreadable status is not permission to deploy.
status_text=$("$cli" --port "$port" --status 2>/dev/null) || true
if grep -Eq '(^|[[:space:]])local_process=true([[:space:]]|$)' <<< "$status_text"; then
  echo "ERROR: Valheim is running; quit it first so the new build is the one that loads" >&2
  exit 5
fi
if ! grep -Eq '(^|[[:space:]])local_process=false([[:space:]]|$)' <<< "$status_text"; then
  echo "ERROR: cannot establish that Valheim is stopped; check --status before deploying" >&2
  exit 5
fi

echo "== build ($config)"
dotnet build "$project" -c "$config" -nologo -v quiet
dll=$(dotnet msbuild "$project" -getProperty:TargetPath -p:Configuration="$config")
[ -f "$dll" ] || { echo "ERROR: the build reported $dll, which does not exist" >&2; exit 1; }

if [ -n "$plan" ]; then
  # Pin the artifact this script deploys, not whatever the game reports.
  md5=$(md5_of "$dll")
  key=${VALHEIM_PLUGIN_KEY:-$(basename "$dll" .dll)}
  derived=$(mktemp "${TMPDIR:-/tmp}/dev-loop-pins.XXXXXX")
  trap 'rm -f "$derived"' EXIT
  if ! derive_pins "$pins" "$key" "$md5" > "$derived"; then
    echo "ERROR: $pins must pin $key exactly once; set VALHEIM_PLUGIN_KEY to the GUID, name or DLL name it uses" >&2
    exit 4
  fi
  echo "== pins: $key=$md5 (derived copy of $pins)"
fi

echo "== install $(basename "$dll") -> $plugins"
cp "$dll" "$plugins/"
pdb="${dll%.dll}.pdb"
[ -f "$pdb" ] && cp "$pdb" "$plugins/"
if [ -n "$plan" ] && [ "$(md5_of "$plugins/$(basename "$dll")")" != "$md5" ]; then
  echo "ERROR: the installed $(basename "$dll") differs from the build" >&2
  exit 1
fi

waits=()
[ -n "${PROGRESS:-}" ] && waits+=(--progress "$PROGRESS")
[ -n "${STALL:-}" ] && waits+=(--stall "$STALL")

status=0
if [ -n "$plan" ]; then
  echo "== launch and run $plan"
  stop=(); [ "${STOP_AFTER:-0}" = 1 ] && stop=(--stop-after)
  "$cli" --port "$port" --expect-strict "$derived" --test "$plan" --launch ${stop[@]+"${stop[@]}"} ${waits[@]+"${waits[@]}"} || status=$?
else
  echo "== launch"
  "$cli" --port "$port" --launch --timeout 300s ${waits[@]+"${waits[@]}"} || status=$?
fi

echo "== log"
"$here/log-summary.sh" || true
exit "$status"
