#!/bin/sh
# Run a maintainer C# file app without relying on an unwritable SDK runfile cache.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
mode=run
if [ "${1:-}" = --build-only ]; then mode=build; shift; fi
[ "$#" -ge 1 ] || { echo 'Usage: sh scripts/run-file-app.sh [--build-only] scripts/NAME.cs [arguments...]' >&2; exit 2; }
script=$1
shift
[ "$mode" = run ] || [ "$#" -eq 0 ] || { echo '--build-only takes no script arguments.' >&2; exit 2; }

if [ "$(uname -s)" = Darwin ]; then
    app_cache="$HOME/Library/Application Support/dotnet/runfile"
else
    app_cache="${XDG_DATA_HOME:-$HOME/.local/share}/dotnet/runfile"
fi
ancestor=$app_cache
while [ ! -d "$ancestor" ]; do ancestor=$(dirname -- "$ancestor"); done
cd -- "$root"
# The force flag exercises the real conversion path in CI without changing a developer's home directory.
if [ "${VT_FORCE_RUNFILE_FALLBACK:-}" != 1 ] && [ -w "$ancestor" ]; then
    if [ "$mode" = build ]; then exec dotnet build "$script"; fi
    exec dotnet run "$script" -- "$@"
fi

printf 'SDK file-app cache unavailable: %s; using a temporary project under artifacts/runfile.\n' "$app_cache" >&2
mkdir -p artifacts/runfile
run_dir=$(mktemp -d "$root/artifacts/runfile/$(basename "$script" .cs)-XXXXXXXX")
project="$run_dir/project"
trap 'rm -rf -- "$run_dir"' EXIT HUP INT TERM
# The SDK converter does not rebase relative #:project paths correctly when it moves a file app.
# Resolve those paths against the original script before conversion; leave the source untouched.
source_dir=$(CDPATH= cd -- "$(dirname -- "$script")" && pwd)
converted_source="$run_dir/$(basename -- "$script")"
while IFS= read -r line || [ -n "$line" ]; do
    case $line in
        '#:project '*)
            reference=${line#'#:project '}
            case $reference in
                /*) absolute_reference=$reference ;;
                *) absolute_reference=$(CDPATH= cd -- "$source_dir/$(dirname -- "$reference")" && pwd)/$(basename -- "$reference") ;;
            esac
            printf '#:project %s\n' "$absolute_reference"
            ;;
        *) printf '%s\n' "$line" ;;
    esac
done < "$script" > "$converted_source"
dotnet project convert "$converted_source" --output "$project"
if [ "$mode" = build ]; then dotnet build "$project"; else dotnet run --project "$project" -- "$@"; fi
