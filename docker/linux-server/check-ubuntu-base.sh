#!/usr/bin/env bash
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
record="$here/ubuntu-base.json"
dockerfile="$here/Dockerfile"

upstream=$(jq -er '.upstream' "$record")
tag=$(jq -er '.tag' "$record")
platform=$(jq -er '.platform' "$record")
digest=$(jq -er '.digest' "$record")
mirror=$(jq -er '.mirror' "$record")
size=$(jq -er '.compressedLayerBytes' "$record")

test "$upstream" = public.ecr.aws/ubuntu/ubuntu
test "$tag" = 24.04
test "$platform" = linux/amd64
test "$mirror" = ghcr.io/tvongaza/valheimtesting-ubuntu-base
[[ "$digest" =~ ^sha256:[0-9a-f]{64}$ ]]
[[ "$size" =~ ^[0-9]+$ ]] && (( size > 0 && size < 50000000 ))

base="$mirror@$digest"
test "$(grep -c '^FROM ' "$dockerfile")" -eq 1
grep -Fxq "FROM --platform=linux/amd64 $base" "$dockerfile"
printf '%s\n' "$base"
