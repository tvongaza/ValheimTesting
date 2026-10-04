#!/usr/bin/env bash
# Installs mono-devel on an Ubuntu runner for the test-runners job, using the archives restored from the Actions cache
# into $RUNNER_TEMP/mono-debs only where they are the archives the signed index names, then copies what apt used back
# there for the cache to save. Needs $RUNNER_TEMP/mono-uris.txt from `apt-get install --print-uris` (the job's key step).
#
# apt installs a same-size archive from its cache without checking it, and --print-uris gives only MD5 sums, so a
# restored archive is used only when its SHA256 is the one the signed index gives; apt downloads the rest.
set -euo pipefail

# <package>=<version> (an epoch's colon is %3a in a file name) and the file name of each archive:
archives() { while read -r _ file _; do v=${file#*_}; id="${file%%_*}=${v%_*}"; printf '%s %s\n' "${id//%3a/:}" "$file"; done < "$RUNNER_TEMP/mono-uris.txt"; }
if [ -d "$RUNNER_TEMP/mono-debs" ]; then
  apt-cache show $(archives | cut -d' ' -f1) | awk '/^Package:/ { p = $2 } /^Version:/ { v = $2 } /^SHA256:/ { print p "=" v, $2 }' > "$RUNNER_TEMP/mono-sha256.txt"
  while read -r id file; do
    f="$RUNNER_TEMP/mono-debs/$file"; [ -f "$f" ] || continue
    want=$(awk -v id="$id" '$1 == id { print $2; exit }' "$RUNNER_TEMP/mono-sha256.txt")
    if [ -n "$want" ] && [ "$(sha256sum < "$f" | cut -d' ' -f1)" = "$want" ]; then sudo cp "$f" /var/cache/apt/archives/
    else echo "Not using the cached $file: it is not the archive the index names."; fi
  done < <(archives)
fi
sudo apt-get install -y -q --no-install-recommends mono-devel
mono --version
mkdir -p "$RUNNER_TEMP/mono-debs"; cp /var/cache/apt/archives/*.deb "$RUNNER_TEMP/mono-debs/"
