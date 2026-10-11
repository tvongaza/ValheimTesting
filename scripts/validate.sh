#!/bin/sh
# Maintainer entry point: diagnose host access before the long no-game validation.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
. "$root/scripts/nuget-caches.sh"

vt_nearest_writable_parent() {
    vt_parent=$1
    while [ ! -d "$vt_parent" ]; do vt_parent=$(dirname -- "$vt_parent"); done
    [ -w "$vt_parent" ]
}

vt_report_cache() {
    vt_name=$1
    vt_path=$2
    vt_fallback=$3
    if vt_nearest_writable_parent "$vt_path"; then
        printf 'READY: NuGet %s cache %s\n' "$vt_name" "$vt_path"
    elif vt_nearest_writable_parent "$vt_fallback"; then
        printf 'FALLBACK: NuGet %s cache %s is unavailable; will use %s\n' "$vt_name" "$vt_path" "$vt_fallback"
    else
        printf 'REFUSED: NuGet %s cache %s and fallback %s are unavailable. Choose a writable cache with NUGET_PACKAGES and NUGET_HTTP_CACHE_PATH.\n' "$vt_name" "$vt_path" "$vt_fallback" >&2
        return 1
    fi
}

vt_probe_host() {
    [ "$(uname -s)" = Darwin ] || return 0
    vt_ok=0
    if vt_ps=$(ps -p "$$" -o pid= 2>&1) && [ -n "$(printf '%s' "$vt_ps" | tr -d '[:space:]')" ]; then
        printf 'READY: macOS process identity (/bin/ps).\n'
    else
        printf 'UNAVAILABLE: macOS process identity (/bin/ps): %s\n' "$vt_ps" >&2
        vt_ok=1
    fi
    if vt_ports=$(netstat -an -p tcp 2>&1) && printf '%s\n' "$vt_ports" | grep -q '^Proto[[:space:]]'; then
        printf 'READY: macOS TCP listener table (netstat).\n'
    else
        printf 'UNAVAILABLE: macOS TCP listener table (netstat): %s\n' "$vt_ports" >&2
        vt_ok=1
    fi
    return "$vt_ok"
}

vt_main() {
    vt_mode=${1:-full}
    case $vt_mode in
        full) [ "$#" -eq 0 ] || { echo 'Usage: sh scripts/validate.sh [--preflight-only|--restricted-sandbox]' >&2; return 2; } ;;
        --preflight-only|--restricted-sandbox) [ "$#" -eq 1 ] || { echo 'Usage: sh scripts/validate.sh [--preflight-only|--restricted-sandbox]' >&2; return 2; } ;;
        *) echo 'Usage: sh scripts/validate.sh [--preflight-only|--restricted-sandbox]' >&2; return 2 ;;
    esac
    cd -- "$root"
    if [ "$(uname -s)" = Darwin ]; then
        vt_app_cache="$HOME/Library/Application Support/dotnet/runfile"
    else
        vt_app_cache="${XDG_DATA_HOME:-$HOME/.local/share}/dotnet/runfile"
    fi
    if vt_nearest_writable_parent "$vt_app_cache"; then
        printf 'READY: SDK file-app cache %s\n' "$vt_app_cache"
    else
        printf 'FALLBACK: SDK file-app cache %s is unavailable; will convert file apps to temporary projects.\n' "$vt_app_cache"
    fi
    vt_caches=0
    vt_report_cache packages "${NUGET_PACKAGES:-$(vt_default_cache global-packages)}" "$root/artifacts/nuget-bootstrap/packages" || vt_caches=1
    vt_report_cache http "${NUGET_HTTP_CACHE_PATH:-$(vt_default_cache http-cache)}" "$root/artifacts/nuget-bootstrap/http" || vt_caches=1
    vt_host=0
    vt_probe_host || vt_host=1
    if [ "$vt_mode" = --preflight-only ]; then
        if [ "$vt_host" -ne 0 ]; then
            printf 'NEXT: run full validation in CI or an authorized Mac shell. --restricted-sandbox can run selected no-host tests but is explicitly incomplete.\n' >&2
        fi
        [ "$vt_caches" -eq 0 ] && [ "$vt_host" -eq 0 ] || return 3
        printf 'READY: full local validation can start.\n'
        return 0
    fi
    [ "$vt_caches" -eq 0 ] || return 3
    if [ "$vt_mode" = full ] && [ "$vt_host" -ne 0 ]; then
        printf 'REFUSED: full validation needs process and port observations. Use CI or an authorized Mac shell; --restricted-sandbox is explicitly incomplete.\n' >&2
        return 3
    fi
    vt_select_nuget_caches "$root/artifacts/nuget-bootstrap" || return 3
    if [ "$vt_mode" = --restricted-sandbox ]; then
        printf 'INCOMPLETE: restricted-sandbox validation runs only selected no-host tests; process, port, recovery and package checks are not proven. Run full validation in CI or an authorized shell.\n'
        NuGetAudit=false
        export NuGetAudit
        sh scripts/run-file-app.sh scripts/validate.cs --restricted-sandbox
        return $?
    fi
    sh scripts/bootstrap-cli.sh
    sh scripts/run-file-app.sh scripts/validate.cs
}

if [ "${VT_VALIDATE_SOURCE_ONLY:-}" != 1 ]; then vt_main "$@"; fi
