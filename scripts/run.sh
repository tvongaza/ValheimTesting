#!/usr/bin/env bash
# Check NuGet's caches before dotnet run restores a file-based script.
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
case "${1:-}" in
  bootstrap) script=bootstrap-cli.cs ;;
  validate) script=validate.cs ;;
  *) printf 'usage: %s {bootstrap|validate} [script arguments]\n' "$0" >&2; exit 2 ;;
esac
shift
cd "$root"

cache_path() {
  local variable=$1 kind=$2 value output
  value=${!variable:-}
  if [[ -n "$value" ]]; then printf '%s\n' "$value"; return; fi
  output=$(dotnet nuget locals "$kind" --list)
  value=${output#*: }
  if [[ "$value" == "$output" || -z "$value" ]]; then
    printf 'Could not query NuGet %s cache path: %s\n' "$kind" "$output" >&2
    return 1
  fi
  printf '%s\n' "$value"
}

can_write() {
  local directory=$1 probe
  mkdir -p "$directory" 2>/dev/null || return 1
  probe=$(mktemp "$directory/.valheimtesting-write-XXXXXXXX" 2>/dev/null) || return 1
  if ! printf x > "$probe"; then rm -f "$probe"; return 1; fi
  rm -f "$probe"
}

packages=$(cache_path NUGET_PACKAGES global-packages)
http=$(cache_path NUGET_HTTP_CACHE_PATH http-cache)
if can_write "$packages" && can_write "$http"; then
  export NUGET_PACKAGES=$packages NUGET_HTTP_CACHE_PATH=$http
  printf 'NuGet caches writable before dotnet run: packages=%s; HTTP=%s\n' "$packages" "$http"
else
  key=$(printf '%s' "$root" | cksum | awk '{print $1}')
  fallback=${TMPDIR:-/tmp}
  fallback=${fallback%/}/valheimtesting-nuget-$(id -u)-$key
  packages=$fallback/packages
  http=$fallback/http-cache
  if ! can_write "$packages" || ! can_write "$http"; then
    printf 'NuGet caches and fallback are not writable; set NUGET_PACKAGES and NUGET_HTTP_CACHE_PATH to writable directories.\n' >&2
    exit 1
  fi
  export NUGET_PACKAGES=$packages NUGET_HTTP_CACHE_PATH=$http
  printf 'NuGet caches blocked before dotnet run; using packages=%s; HTTP=%s\n' "$packages" "$http"
fi

dotnet restore "scripts/$script" --force
exec dotnet run "scripts/$script" --no-restore -- "$@"
