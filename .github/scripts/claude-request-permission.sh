#!/usr/bin/env bash
set -euo pipefail
: "${GH_REPO:?GH_REPO must be set by the workflow}"
: "${GITHUB_OUTPUT:?GITHUB_OUTPUT must be set by the workflow}"
REQUEST_ACTOR=${REQUEST_ACTOR:-}
REQUEST_AUTHOR=${REQUEST_AUTHOR:-}
REQUEST_PR_NUMBER=${REQUEST_PR_NUMBER:-}
REQUEST_PR_AUTHOR=${REQUEST_PR_AUTHOR:-}

has_write_permission() {
  local login=$1 permission
  [[ -n $login ]] || return 1
  # Missing collaborators, bots, transient API failures, and malformed responses
  # deny authorization without turning an untrusted request into a failed run.
  if ! permission=$(gh api "repos/$GH_REPO/collaborators/$login/permission" --jq .permission 2>/dev/null); then
    return 1
  fi
  case "$permission" in
    admin|maintain|write) return 0 ;;
    *) return 1 ;;
  esac
}

authorized=false
# Assignment is initiated by the maintainer, but the issue text still belongs
# to its original author. Neither identity can lend permission to the other.
# PR code also enters the OAuth workspace, so its author needs write access.
if has_write_permission "$REQUEST_ACTOR" &&
    { [[ $REQUEST_AUTHOR == "$REQUEST_ACTOR" ]] || has_write_permission "$REQUEST_AUTHOR"; } &&
    { [[ -z $REQUEST_PR_NUMBER ]] || has_write_permission "$REQUEST_PR_AUTHOR"; }; then
  authorized=true
fi
printf 'authorized=%s\n' "$authorized" >> "$GITHUB_OUTPUT"
