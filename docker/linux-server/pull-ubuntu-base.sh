#!/usr/bin/env bash
set -euo pipefail

base=$(bash "$(dirname "$0")/check-ubuntu-base.sh")
if ! docker pull --platform linux/amd64 "$base"; then
  echo "::error::Could not fetch the pinned plain Ubuntu base $base from GHCR. Check the mirror package and this job's packages: read permission." >&2
  exit 1
fi
