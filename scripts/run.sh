#!/usr/bin/env bash
# Check NuGet's caches before any dotnet run; file-based scripts also need the SDK's app state.
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
case "${1:-}" in
  bootstrap) script=bootstrap-cli.cs ;;
  validate) script=validate.cs ;;
  api-docs) script=api-docs.cs ;;
  campaign) script=campaign ;;
  *) printf 'usage: %s {bootstrap|validate|api-docs|campaign} [script arguments]\n' "$0" >&2; exit 2 ;;
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

if [[ "$script" == campaign ]]; then
  exec dotnet run --project examples/FullLifecycle/MyMod.SystemTests -c Release -- campaign "$@"
fi

# The SDK builds a file-based script under a per-user directory that no setting moves (#210): the temporary directory
# on Windows, LocalApplicationData elsewhere. .NET reads that on macOS from the account, not $HOME; on Linux it honours
# an absolute XDG_DATA_HOME, then HOME, then the account's home.
home=${HOME:-}
if [[ "$(uname -s)" == Darwin || -z "$home" ]]; then
  user=$(id -un)
  [[ "$user" =~ ^[A-Za-z0-9._-]+$ ]] && eval "home=~$user"
fi
if [[ "$(uname -s)" == Darwin ]]; then
  runfile="$home/Library/Application Support/dotnet/runfile"
elif [[ "${XDG_DATA_HOME:-}" == /* ]]; then
  runfile=$XDG_DATA_HOME/dotnet/runfile
else
  runfile=$home/.local/share/dotnet/runfile
fi
shown=$runfile
[[ -n "$home" && "$runfile" == "$home"/* ]] && shown="~${runfile#"$home"}"

# The SDK creates one owner-only directory per script there.
can_create_directory() {
  local directory=$1 probe
  (umask 077 && mkdir -p "$directory") 2>/dev/null || return 1
  probe=$(mktemp -d "$directory/.valheimtesting-write-XXXXXXXX" 2>/dev/null) || return 1
  rmdir "$probe"
}

if can_create_directory "$runfile"; then
  printf 'File-based app state writable: %s\n' "$shown"
  dotnet restore "scripts/$script" --force
  exec dotnet run "scripts/$script" --no-restore -- "$@"
fi

# Run the script as the project the SDK converts it to: that builds in the workspace instead. Project files are
# replaced only when the conversion changes, so its build is reused across runs.
name=${script%.cs}
project=artifacts/runfile/$name
printf 'File-based app state not writable: %s; running scripts/%s as the project %s\n' "$shown" "$script" "$project"
mkdir -p "$project"
converted=$(mktemp -d "$project.new-XXXXXXXX")
trap 'rm -rf "$converted"' EXIT
rmdir "$converted"
if ! dotnet project convert "scripts/$script" --output "$converted" >/dev/null; then
  printf 'Could not convert scripts/%s to a project. Grant write access to %s; no SDK setting moves it.\n' "$script" "$shown" >&2
  exit 1
fi
for file in "$project"/*; do
  if [[ -f "$file" && ! -e "$converted/${file##*/}" ]]; then rm "$file"; fi
done
for file in "$converted"/*; do
  cmp -s "$file" "$project/${file##*/}" || cp "$file" "$project/"
done
rm -rf "$converted"
trap - EXIT
dotnet restore "$project/$name.csproj" --force
exec dotnet run --project "$project/$name.csproj" --no-restore -- "$@"
