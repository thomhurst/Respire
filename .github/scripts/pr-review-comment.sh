#!/usr/bin/env bash
# Review-posting helper for the Claude Code Review workflow.
#
# That workflow runs on pull_request_target so it can review pull requests from
# forks, which means the diff it analyses is untrusted while the job holds real
# `pull-requests: write`. This script is the only write path exposed to the
# model: the pull request number comes from the environment rather than an
# argument, so an injected instruction cannot retarget another PR, and the body
# is passed directly rather than read from a path, so no file on the runner can
# be turned into a public comment.
#
# Usage:
#   pr-review-comment.sh "<markdown body>"
set -euo pipefail

: "${PR_NUMBER:?PR_NUMBER must be set by the workflow}"
: "${GH_REPO:?GH_REPO must be set by the workflow}"
: "${REVIEW_HEAD_SHA:?REVIEW_HEAD_SHA must be set by the workflow}"
if [[ ! $REVIEW_HEAD_SHA =~ ^[0-9a-f]{40}$ ]]; then
  echo "invalid reviewed commit SHA" >&2
  exit 2
fi

body=${1:-}

if [[ -z ${body//[[:space:]]/} ]]; then
  echo "refusing to post an empty review comment" >&2
  exit 2
fi
head_marker_pattern='<!--[[:space:]]*REVIEW_HEAD_SHA[[:space:]]*:'
if [[ ${body^^} =~ $head_marker_pattern ]]; then
  echo "the helper owns the reviewed commit marker" >&2
  exit 2
fi
current_head=$(gh pr view "$PR_NUMBER" --repo "$GH_REPO" --json headRefOid --jq .headRefOid)
if [[ $current_head != "$REVIEW_HEAD_SHA" ]]; then
  echo "pull request changed during review; refusing to post stale clearance" >&2
  exit 1
fi

# The hidden marker lets scripts/Assert-PrGreen.ps1 recognise this comment as
# the Claude review, since it is posted by the generic workflow token.
marker='<!-- claude-code-review -->'
body+=$'\n\n'"$marker"
body+=$'\n'"<!-- REVIEW_HEAD_SHA: $REVIEW_HEAD_SHA -->"

gh pr comment "$PR_NUMBER" --repo "$GH_REPO" --body "$body"
