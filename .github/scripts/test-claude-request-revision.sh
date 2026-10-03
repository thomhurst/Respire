#!/usr/bin/env bash
set -euo pipefail
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
workflow="$script_dir/../workflows/claude.yml"
test_dir=$(mktemp -d)
trap 'rm -f "$test_dir/output"; rmdir "$test_dir"' EXIT
head=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
event=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
count=0
check_revision() {
  local name=$1 expected=$2
  export REQUEST_PR_HEAD_SHA=$3 REQUEST_PR_NUMBER=$4 REQUEST_EVENT_SHA=$5
  export GITHUB_OUTPUT="$test_dir/output"
  : > "$GITHUB_OUTPUT"
  if bash "$script_dir/claude-request-revision.sh"; then
    [[ -n $expected && $(cat "$GITHUB_OUTPUT") == "ref=$expected" ]] || {
      echo "FAIL: $name"; exit 1;
    }
  else
    [[ -z $expected && ! -s $GITHUB_OUTPUT ]] || { echo "FAIL: $name"; exit 1; }
  fi
  count=$((count + 1))
}
check_revision 'review event uses exact PR head' "$head" "$head" '' "$event"
check_revision 'PR issue comment uses PR ref' refs/pull/801/head '' 801 "$event"
check_revision 'plain issue uses event commit' "$event" '' '' "$event"
check_revision 'PR head takes precedence' "$head" "$head" 801 "$event"
check_revision 'invalid head cannot fall back' '' main 801 "$event"
check_revision 'invalid PR number cannot fall back' '' '' '801/merge' "$event"
check_revision 'zero PR number rejected' '' '' 0 "$event"
check_revision 'invalid event rejected' '' '' '' main
check_revision 'missing event rejected' '' '' '' ''
check_revision 'multiline head rejected' '' "$head"$'\nref=main' '' "$event"

# Guard the trust boundary: the helper must run before the second checkout,
# and both revision selection and that checkout must require authorization.
awk '
  /id: permission/ { permission = NR }
  /id: revision/ { revision = NR; in_revision = 1 }
  /name: Checkout requested revision/ { checkout = NR; in_revision = 0; in_checkout = 1 }
  /name: Run Claude Code/ { claude = NR; in_checkout = 0 }
  /if: steps.permission.outputs.authorized ==/ {
    if (in_revision) revision_guard = 1
    if (in_checkout) checkout_guard = 1
  }
  END { exit !(permission < revision && revision < checkout && checkout < claude && revision_guard && checkout_guard) }
' "$workflow"
grep -Fq 'REQUEST_PR_HEAD_SHA: ${{ github.event.pull_request.head.sha }}' "$workflow"
grep -Fq 'REQUEST_PR_NUMBER: ${{ github.event.issue.pull_request && github.event.issue.number ||' "$workflow"
grep -Fq 'REQUEST_EVENT_SHA: ${{ github.sha }}' "$workflow"
grep -Fq 'run: bash .github/scripts/claude-request-revision.sh' "$workflow"
grep -Fq 'ref: ${{ steps.revision.outputs.ref }}' "$workflow"
echo "OK Claude request revision tests passed ($count cases and workflow trust boundary)."
