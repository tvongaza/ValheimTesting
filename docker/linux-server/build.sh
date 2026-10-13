#!/usr/bin/env bash
# Builds the private image from the repository's pinned BepInExPack (loader-dependency.json), not from a download of its own.
#
#   dotnet run scripts/bootstrap-cli.cs          (fetches and checks .packages/bepinexpack-valheim.zip)
#   bash docker/linux-server/build.sh -t valheimtesting-linux-server:local [more docker build arguments]
#
# The zip enters the build as the named context "pinned" (needs BuildKit, Docker 23 or later; a plain `docker build` of the
# Dockerfile fails with "pull access denied ... pinned") and the Dockerfile checks its SHA-256 against the pin before extracting it. The arguments after the script's name go to `docker build`; a later
# --build-arg BEPINEX_PACK_SHA256=... replaces the pin's hash (the scheduled checks' negative control).
set -euo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
if [ ! -f "$root/.packages/bepinexpack-valheim.zip" ]; then
  echo "::error::.packages/bepinexpack-valheim.zip is missing. Run scripts/bootstrap-cli.cs first: it fetches the pinned BepInExPack and checks its SHA-256." >&2
  exit 1
fi
sha256=$(sed -n 's/.*"sha256"[[:space:]]*:[[:space:]]*"\([0-9a-fA-F]\{64\}\)".*/\1/p' "$root/loader-dependency.json" | head -n 1 | tr 'A-F' 'a-f')
if [ -z "$sha256" ]; then
  echo "::error::loader-dependency.json has no sha256." >&2
  exit 1
fi
# Only the zip enters the build, not the rest of the local feed.
stage=$(mktemp -d)
trap 'rm -rf "$stage"' EXIT
cp "$root/.packages/bepinexpack-valheim.zip" "$stage/"
docker build --build-context "pinned=$stage" --build-arg "BEPINEX_PACK_SHA256=$sha256" "$@" "$root/docker/linux-server"
