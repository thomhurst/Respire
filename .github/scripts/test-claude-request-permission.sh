#!/usr/bin/env bash
set -euo pipefail
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
workflow="$script_dir/../workflows/claude.yml"
grep -Fq 'ref: ${{ github.event.repository.default_branch }}' "$workflow"
grep -Fq 'REQUEST_AUTHOR: ${{ github.event.comment.user.login || github.event.review.user.login || github.event.issue.user.login }}' "$workflow"
grep -Fq 'run: bash .github/scripts/claude-request-permission.sh' "$workflow"
test_dir=$(mktemp -d)
trap 'rm -f "$test_dir/gh" "$test_dir/output" "$test_dir/queries"; rmdir "$test_dir"' EXIT
cat > "$test_dir/gh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
[[ $1 == api && $3 == --jq && $4 == .permission ]] || exit 97
login=${2%/permission}
login=${login##*/}
printf '%s\n' "$login" >> "$TEST_QUERIES"
if [[ $login == "$REQUEST_ACTOR" ]]; then
  permission=$TEST_ACTOR_PERMISSION
elif [[ $login == "$REQUEST_AUTHOR" ]]; then
  permission=$TEST_AUTHOR_PERMISSION
else
  exit 98
fi
if [[ $permission == api-error ]]; then
  # Even partial success-looking stdout must never authorize a failed API call.
  echo write
  exit 1
fi
printf '%s\n' "$permission"
EOF
chmod +x "$test_dir/gh"
export PATH="$test_dir:$PATH" GH_REPO=example/repo
export GITHUB_OUTPUT="$test_dir/output" TEST_QUERIES="$test_dir/queries"
export REQUEST_ACTOR=maintainer REQUEST_AUTHOR=author
export TEST_ACTOR_PERMISSION TEST_AUTHOR_PERMISSION
cases=0
check_permission() {
  TEST_ACTOR_PERMISSION=$1 TEST_AUTHOR_PERMISSION=$2
  : > "$GITHUB_OUTPUT"
  : > "$TEST_QUERIES"
  bash "$script_dir/claude-request-permission.sh"
  if [[ $(cat "$GITHUB_OUTPUT") != "authorized=$3" ]]; then
    echo "permission case failed: actor=$REQUEST_ACTOR/$1 author=$REQUEST_AUTHOR/$2 expected=$3" >&2
    exit 1
  fi
  cases=$((cases + 1))
}
check_permission admin write true
check_permission maintain admin true
check_permission write maintain true
check_permission read write false
# Assignment by a maintainer must not authorize an external issue author's text.
check_permission write read false
check_permission write triage false
check_permission write none false
check_permission write api-error false
check_permission api-error write false
check_permission unknown write false
check_permission write unknown false
check_permission write '' false
check_permission write $'write\nadmin' false
REQUEST_AUTHOR=$REQUEST_ACTOR
check_permission write api-error true
[[ $(wc -l < "$TEST_QUERIES") -eq 1 ]]
REQUEST_AUTHOR=''
check_permission write write false
REQUEST_AUTHOR=author REQUEST_ACTOR=''
check_permission write write false
REQUEST_ACTOR='dependabot[bot]'
check_permission api-error write false
echo "OK request permission tests passed ($cases cases)"
