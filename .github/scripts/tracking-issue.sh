#!/usr/bin/env bash
# Opens or updates the ONE open tracking issue for failing scheduled native server checks. Used by
# .github/workflows/native-server-checks.yml. Only an open issue that this workflow opened counts: its exact title,
# author github-actions[bot] and both labels (ci, native). Anyone can open an issue with that title on a public
# repository, and such an issue is ignored. The issues list API is used, not the search index, so an issue opened
# moments ago is found too. One such issue gets a comment, none gets a new issue. With DRY_RUN=true nothing is
# written: it prints what it would post, and needs only read access.
#
#   tracking-issue.sh               Environment: GH_TOKEN, GH_REPO, DRY_RUN (true|false), RUN_URL, EVENT, BUILD_ID,
#                                   COMMIT, SUMMARY_FILE.
#   tracking-issue.sh --self-test   Checks the lookup against fixed issue lists; needs only jq.
set -euo pipefail

title="Scheduled native server checks are failing"
bot="github-actions[bot]"
# The lowest-numbered open issue with the exact title, opened by the workflow and labelled ci and native; pull requests
# (which the issues API also lists) never count.
lookup='map(select(.pull_request == null and .title == $title and .user.login == $bot
  and any(.labels[]; .name == "ci") and any(.labels[]; .name == "native"))) | sort_by(.number) | .[0].number // empty'
find_issue() { jq -r --arg title "$title" --arg bot "$bot" "$lookup"; }

if [ "${1:-}" = --self-test ]; then
  issue() { # <number> <title> <author> <labels as a JSON array> [pull request]
    jq -n --argjson number "$1" --arg title "$2" --arg login "$3" --argjson labels "$4" --arg pr "${5:-}" \
      '{number: $number, title: $title, user: {login: $login}, labels: ($labels | map({name: .}))} + (if $pr == "" then {} else {pull_request: {}} end)'
  }
  both='["ci","native"]'
  others=$(jq -s . <(issue 3 "$title" someone "$both") <(issue 4 "$title" "$bot" '["ci"]') <(issue 5 "$title" "$bot" "$both" pr) \
    <(issue 6 "Other title" "$bot" "$both"))
  [ -z "$(printf '%s' "$others" | find_issue)" ] || { echo "Self-test failed: a same-titled issue by someone else, an issue without both labels, a pull request or another title was taken." >&2; exit 1; }
  ours=$(jq -s '.[0] + [.[1], .[2]]' <(printf '%s' "$others") <(issue 9 "$title" "$bot" "$both") <(issue 7 "$title" "$bot" "$both"))
  [ "$(printf '%s' "$ours" | find_issue)" = 7 ] || { echo "Self-test failed: the workflow's own lowest-numbered issue was not found." >&2; exit 1; }
  echo "Lookup self-test passed: a same-titled issue by someone else, a bot issue without both labels, a pull request and another title are ignored; the workflow's own lowest-numbered issue is found."
  exit 0
fi

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

existing=$(gh api --paginate "repos/$GH_REPO/issues?state=open&labels=ci,native&per_page=100" | jq -s 'add // []' | find_issue)

if [ "$DRY_RUN" = true ]; then
  echo "DRY RUN: no issue is created or changed."
  if [ -n "$existing" ]; then echo "Would comment on #$existing ($title):"; else echo "Would open a new issue \"$title\" (labels: ci, native); no open issue with that title opened by $bot:"; fi
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
