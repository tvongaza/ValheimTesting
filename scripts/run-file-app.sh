#!/bin/sh
# Run a maintainer C# file app without relying on an unwritable SDK runfile cache.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
script=$1
shift

if [ "$(uname -s)" = Darwin ]; then
    app_cache="$HOME/Library/Application Support/dotnet/runfile"
else
    app_cache="${XDG_DATA_HOME:-$HOME/.local/share}/dotnet/runfile"
fi
ancestor=$app_cache
while [ ! -d "$ancestor" ]; do ancestor=$(dirname -- "$ancestor"); done
cd -- "$root"
if [ -w "$ancestor" ]; then
    exec dotnet run "$script" -- "$@"
fi

printf 'SDK file-app cache unavailable: %s; using a temporary project under artifacts/runfile.\n' "$app_cache" >&2
mkdir -p artifacts/runfile
run_dir=$(mktemp -d "$root/artifacts/runfile/$(basename "$script" .cs)-XXXXXXXX")
project="$run_dir/project"
trap 'rm -rf -- "$run_dir"' EXIT HUP INT TERM
dotnet project convert "$script" --output "$project"
dotnet run --project "$project" -- "$@"
