#!/bin/sh
# Set writable NuGet caches before dotnet restores the file-based bootstrap app.
set -eu
root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)

. "$root/scripts/nuget-caches.sh"
vt_select_nuget_caches "$root/artifacts/nuget-bootstrap"

if [ "${1:-}" = --check-caches ]; then exit 0; fi
cd -- "$root"
exec sh scripts/run-file-app.sh scripts/bootstrap-cli.cs "$@"
