#!/usr/bin/env bash
set -euo pipefail
: "${GITHUB_OUTPUT:?GITHUB_OUTPUT is required}"

# Run from the trusted checkout after authorization, before checking out PR code.
if [[ -n ${REQUEST_PR_HEAD_SHA:-} ]]; then
  [[ $REQUEST_PR_HEAD_SHA =~ ^[0-9a-fA-F]{40}$ ]] || exit 1
  revision=$REQUEST_PR_HEAD_SHA
elif [[ -n ${REQUEST_PR_NUMBER:-} ]]; then
  [[ $REQUEST_PR_NUMBER =~ ^[1-9][0-9]*$ ]] || exit 1
  revision="refs/pull/$REQUEST_PR_NUMBER/head"
else
  [[ ${REQUEST_EVENT_SHA:-} =~ ^[0-9a-fA-F]{40}$ ]] || exit 1
  revision=$REQUEST_EVENT_SHA
fi
printf 'ref=%s\n' "$revision" >> "$GITHUB_OUTPUT"
