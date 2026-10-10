#!/bin/sh
# Set writable NuGet caches before dotnet restores the file-based bootstrap app.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)

default_cache() {
    dotnet nuget locals "$1" --list | sed 's/^[^:]*: //'
}

writable() {
    mkdir -p -- "$1" 2>/dev/null || return 1
    probe="$1/.valheimtesting-write-$$"
    (umask 077; : > "$probe") 2>/dev/null || return 1
    rm -f -- "$probe"
}

choose_cache() {
    label=$1
    candidate=$2
    fallback=$3
    if ! writable "$candidate"; then
        candidate=$fallback
        if ! writable "$candidate"; then
            printf 'Cannot write either %s cache (%s) or fallback (%s)\n' "$label" "$2" "$fallback" >&2
            exit 2
        fi
    fi
    printf '%s\n' "$candidate"
}

packages=${NUGET_PACKAGES:-$(default_cache global-packages)}
http=${NUGET_HTTP_CACHE_PATH:-$(default_cache http-cache)}
NUGET_PACKAGES=$(choose_cache packages "$packages" "$root/artifacts/nuget-bootstrap/packages")
NUGET_HTTP_CACHE_PATH=$(choose_cache http "$http" "$root/artifacts/nuget-bootstrap/http")
export NUGET_PACKAGES NUGET_HTTP_CACHE_PATH
printf 'NuGet packages: %s\nNuGet HTTP cache: %s\n' "$NUGET_PACKAGES" "$NUGET_HTTP_CACHE_PATH"

if [ "${1:-}" = --check-caches ]; then exit 0; fi
cd -- "$root"
exec dotnet run scripts/bootstrap-cli.cs -- "$@"
