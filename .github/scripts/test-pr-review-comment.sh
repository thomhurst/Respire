#!/usr/bin/env bash
set -euo pipefail
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
test_dir=$(mktemp -d)
trap 'rm -f "$test_dir/gh" "$test_dir/git" "$test_dir/comment"; rmdir "$test_dir"' EXIT
cat > "$test_dir/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
case "$1 $2" in
  'pr view') echo "$TEST_CURRENT_HEAD" ;;
  'pr comment') printf '%s' "${@: -1}" > "$TEST_COMMENT" ;;
  *) exit 97 ;;
esac
EOF
chmod +x "$test_dir/gh"
cat > "$test_dir/git" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
[[ $1 == -C && $2 == */pr-head && $3 == rev-parse && $4 == --verify && $5 == HEAD ]] || exit 98
echo "$TEST_CHECKOUT_HEAD"
exit "$TEST_GIT_EXIT"
EOF
chmod +x "$test_dir/git"
export PATH="$test_dir:$PATH" PR_NUMBER=123 GH_REPO=example/repo
export REVIEW_HEAD_SHA=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
export TEST_CURRENT_HEAD=$REVIEW_HEAD_SHA TEST_COMMENT="$test_dir/comment"
export TEST_CHECKOUT_HEAD=$REVIEW_HEAD_SHA TEST_GIT_EXIT=0
bash "$script_dir/pr-review-comment.sh" '<!-- REVIEW_VERDICT: CLEAR -->'
grep -Fx '<!-- claude-code-review -->' "$TEST_COMMENT"
grep -Fx "<!-- REVIEW_HEAD_SHA: $REVIEW_HEAD_SHA -->" "$TEST_COMMENT"
rm "$TEST_COMMENT"
bash "$script_dir/pr-review-comment.sh" 'The REVIEW_HEAD_SHA marker binds clearance to the reviewed commit.'
grep -Fx 'The REVIEW_HEAD_SHA marker binds clearance to the reviewed commit.' "$TEST_COMMENT"
rm "$TEST_COMMENT"
export TEST_CURRENT_HEAD=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
if bash "$script_dir/pr-review-comment.sh" '<!-- REVIEW_VERDICT: CLEAR -->'; then exit 1; fi
test ! -e "$TEST_COMMENT"
export REVIEW_HEAD_SHA=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
export TEST_CURRENT_HEAD=$REVIEW_HEAD_SHA
if bash "$script_dir/pr-review-comment.sh" '<!-- REVIEW_VERDICT: CLEAR -->'; then exit 1; fi
test ! -e "$TEST_COMMENT"
export REVIEW_HEAD_SHA=$TEST_CHECKOUT_HEAD TEST_CURRENT_HEAD=$TEST_CHECKOUT_HEAD TEST_GIT_EXIT=1
if bash "$script_dir/pr-review-comment.sh" '<!-- REVIEW_VERDICT: CLEAR -->'; then exit 1; fi
test ! -e "$TEST_COMMENT"
export TEST_GIT_EXIT=0
export TEST_CURRENT_HEAD=$REVIEW_HEAD_SHA
if bash "$script_dir/pr-review-comment.sh" "<!-- REVIEW_HEAD_SHA: $REVIEW_HEAD_SHA -->"; then exit 1; fi
test ! -e "$TEST_COMMENT"
if bash "$script_dir/pr-review-comment.sh" "<!-- review_head_sha: $REVIEW_HEAD_SHA -->"; then exit 1; fi
test ! -e "$TEST_COMMENT"
if bash "$script_dir/pr-review-comment.sh" $'<!--\nREVIEW_HEAD_SHA: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa -->'; then exit 1; fi
test ! -e "$TEST_COMMENT"
export REVIEW_HEAD_SHA=invalid
if bash "$script_dir/pr-review-comment.sh" '<!-- REVIEW_VERDICT: CLEAR -->'; then exit 1; fi
test ! -e "$TEST_COMMENT"
echo 'OK review comment helper tests passed'
