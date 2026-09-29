#!/usr/bin/env bash
# Opens or updates the ONE open tracking issue for failing scheduled native server checks. Used by
# .github/workflows/native-server-checks.yml. The issue is found by its exact title among open issues (the list API,
# not the search index, so an issue opened moments ago is found too): one open issue gets a comment, none gets a new
# issue. With DRY_RUN=true nothing is written: it prints what it would post, and needs only read access.
#
# Environment: GH_TOKEN, GH_REPO, DRY_RUN (true|false), RUN_URL, EVENT, BUILD_ID, COMMIT, SUMMARY_FILE.
set -euo pipefail

title="Scheduled native server checks are failing"
body=$(mktemp)
{
  echo "The scheduled native server checks failed. Opened and updated by the workflow; close this issue once they pass again."
  echo
  echo "- Run: $RUN_URL ($EVENT)"
  echo "- Dedicated server build: ${BUILD_ID:-unknown (the image did not build)}"
  echo "- Commit: $COMMIT"
  echo
  if [ -s "${SUMMARY_FILE:-}" ]; then cat "$SUMMARY_FILE"; else echo "No check summary: see the run's log."; fi
} > "$body"

existing=$(gh issue list --repo "$GH_REPO" --state open --limit 1000 --json number,title \
  --jq "map(select(.title == \"$title\")) | sort_by(.number) | .[0].number // empty")

if [ "$DRY_RUN" = true ]; then
  echo "DRY RUN: no issue is created or changed."
  if [ -n "$existing" ]; then echo "Would comment on #$existing ($title):"; else echo "Would open a new issue \"$title\" (labels: ci, native):"; fi
  echo "----"
  cat "$body"
  echo "----"
  exit 0
fi

if [ -n "$existing" ]; then
  gh issue comment "$existing" --repo "$GH_REPO" --body-file "$body"
  echo "Commented on #$existing."
else
  gh issue create --repo "$GH_REPO" --title "$title" --label ci --label native --body-file "$body"
fi
