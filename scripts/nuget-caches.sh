#!/bin/sh
# Shared cache selection for the two maintainer entry points. The probe leaves no file behind.
vt_default_cache() {
    dotnet nuget locals "$1" --list | sed 's/^[^:]*: //'
}

vt_writable_cache() {
    mkdir -p -- "$1" 2>/dev/null || return 1
    vt_probe="$1/.valheimtesting-write-$$"
    (umask 077; : > "$vt_probe") 2>/dev/null || return 1
    rm -f -- "$vt_probe"
}

vt_choose_cache() {
    vt_label=$1
    vt_candidate=$2
    vt_fallback=$3
    if ! vt_writable_cache "$vt_candidate"; then
        printf 'NuGet %s cache unavailable: %s; trying %s\n' "$vt_label" "$vt_candidate" "$vt_fallback" >&2
        vt_candidate=$vt_fallback
        if ! vt_writable_cache "$vt_candidate"; then
            printf 'Cannot write either NuGet %s cache (%s) or fallback (%s)\n' "$vt_label" "$2" "$vt_fallback" >&2
            return 2
        fi
    fi
    printf '%s\n' "$vt_candidate"
}

vt_select_nuget_caches() {
    vt_cache_root=$1
    vt_packages=${NUGET_PACKAGES:-$(vt_default_cache global-packages)}
    vt_http=${NUGET_HTTP_CACHE_PATH:-$(vt_default_cache http-cache)}
    NUGET_PACKAGES=$(vt_choose_cache packages "$vt_packages" "$vt_cache_root/packages") || return $?
    NUGET_HTTP_CACHE_PATH=$(vt_choose_cache http "$vt_http" "$vt_cache_root/http") || return $?
    export NUGET_PACKAGES NUGET_HTTP_CACHE_PATH
    printf 'NuGet packages: %s\nNuGet HTTP cache: %s\n' "$NUGET_PACKAGES" "$NUGET_HTTP_CACHE_PATH"
}
