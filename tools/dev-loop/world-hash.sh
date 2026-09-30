#!/usr/bin/env bash
# Hash a Valheim world's save folder the way cli_world does, to prove which
# saved state a game loaded (its `files=` field and the `worldfiles` key).
#
#   world-hash.sh WORLD              hash VALHEIM_SAVES/WORLD
#   world-hash.sh /path/to/folder    hash that folder
#   world-hash.sh WORLD --compare    also ask the running game (cli_world) and
#                                    exit 1 when the two differ
#
# Recipe (ValheimCLI's docs/expectations.md, "World files hash"): for every
# file under the folder, recursively, the line
# "relative/path:md5"; lines sorted in byte order, joined by \n with no
# trailing newline; the md5 of that text. Uses md5sum (Linux) or md5 (macOS).
#
# Use it on a copy you restore before each run: hash the copy, then compare
# with the game after it loads. The game hashes the folder just before it
# loads the world, so any later save does not change its answer; this script
# hashes the folder as it is now.
#
# Environment:
#   VALHEIM_SAVES     the worlds_local folder (default: the Steam save folder,
#                     ~/Library/Application Support/IronGate/Valheim/worlds_local
#                     on macOS, ~/.config/unity3d/IronGate/Valheim/worlds_local
#                     on Linux; a dedicated server started with -savedir keeps
#                     its worlds under that folder instead)
#   VALHEIM_CLI       path to the valheim-cli executable (default: valheim-cli on PATH)
#   VALHEIM_CLI_PORT  the game's CLI port (default 5555), for --compare
#
# --compare needs the game running as the server or host (a client has no
# world files). For a game on another machine, forward its port first:
# ssh -N -L 5555:127.0.0.1:5555 host
#
# Exit code: 0 hashed (and with --compare, the game agrees); 1 the game
# reported another hash or none; valheim-cli's own code when the game could
# not be asked (3: no connection); 4 usage, or no folder to hash.
#
# world-hash.ps1 is its PowerShell twin for Windows. Moved from ValheimCLI
# (commit d112140) to ValheimTesting on 30 Sep 2026; valheim-cli itself comes
# from a ValheimCLI release or build, see tools/dev-loop/README.md.
set -euo pipefail

if [[ "$(uname -s)" == "Darwin" ]]; then
  default_saves="$HOME/Library/Application Support/IronGate/Valheim/worlds_local"
else
  default_saves="$HOME/.config/unity3d/IronGate/Valheim/worlds_local"
fi
saves="${VALHEIM_SAVES:-$default_saves}"
cli="${VALHEIM_CLI:-valheim-cli}"
port="${VALHEIM_CLI_PORT:-5555}"

if [[ $# -lt 1 ]]; then
  sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//' >&2
  exit 4
fi

target="$1"
compare="${2:-}"
if [[ $# -gt 2 || ( -n "$compare" && "$compare" != "--compare" ) ]]; then
  sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//' >&2
  exit 4
fi
if [[ -d "$target" ]]; then
  dir="$target"
else
  dir="$saves/$target"
fi
if [[ ! -d "$dir" ]]; then
  echo "ERROR: no world folder at $dir" >&2
  exit 4
fi

# md5 of stdin, lower-case hex only.
md5_hex() {
  if command -v md5sum >/dev/null 2>&1; then
    md5sum | cut -d' ' -f1
  else
    md5 -q
  fi
}

lines="$(
  cd "$dir"
  find . -type f | while IFS= read -r f; do
    printf '%s:%s\n' "${f#./}" "$(md5_hex < "$f")"
  done | LC_ALL=C sort
)"
# $(...) dropped the final newline, so the lines are joined by \n with none trailing.
hash="$(printf '%s' "$lines" | md5_hex)"
echo "$hash  $dir"

if [[ "$compare" == "--compare" ]]; then
  game="$("$cli" --port "$port" cli_world | sed -n 's/^WORLD .* files=\([^ ]*\) .*/\1/p')"
  if [[ -z "$game" || "$game" == "-" ]]; then
    echo "ERROR: the game reported no load-time hash (not a server or host, or no world loaded)" >&2
    exit 1
  fi
  if [[ "$game" == "$hash" ]]; then
    echo "OK: the game loaded this saved state"
  else
    echo "MISMATCH: the game loaded $game" >&2
    exit 1
  fi
fi
